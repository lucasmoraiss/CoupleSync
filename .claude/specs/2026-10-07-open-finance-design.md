# Open Finance no CoupleSync via Meu Pluggy — desenho

Data: 2026-10-07. Decidido com o dono nesta sessão. Este documento é a referência das issues de
Open Finance; cada fase da seção 9 vira uma issue com o rótulo `backlog` e passa pela esteira `/entregar`.

## 1. Objetivo e limites

Trazer para o CoupleSync, sem digitação, os dados bancários de cada pessoa do grupo (extrato, cartão e
faturas, saldos, investimentos), usando o Meu Pluggy como ponte gratuita ao Open Finance Brasil. Esses
dados alimentam o que o app já tem (transações, rendas, painel, fluxo de caixa) e formam a base de dados
para a IA sugerir onde economizar, apontar gastos recorrentes e comentar a carteira.

Vale para o grupo fechado de hoje (dono, parceira, testers). Não há homologação comercial nem escala.

Fatos do Pluggy que moldam o desenho (conferidos em 2026-10-07 em pluggy.ai/meu-pluggy, meu.pluggy.ai/api-guide,
github.com/pluggyai/meu-pluggy e docs.pluggy.ai):

- Meu Pluggy: gratuito por tempo indeterminado para dados do próprio titular; até 5 conexões; atualização
  automática a cada 24 horas; "uso pessoal, só o titular". Inclui saldo, extrato, cartão e investimentos.
- Credenciais: conta no Dashboard (dashboard.pluggy.ai) → aplicação → Client ID e Client Secret. Na aplicação,
  ligar o conector "MeuPluggy"; na Demo, "Conectar conta" com login do Meu Pluggy (OAuth) → um Item por banco;
  "Copiar Item ID" no menu de três pontos. Sem o conector MeuPluggy ligado a API responde lista vazia.
- Autenticação: `POST /auth` com client id e secret → `apiKey` válida por 2 horas, enviada em `X-API-KEY`.
- Dados: `GET /items/{id}`, `GET /accounts?itemId=`, `GET /transactions?accountId=&from=&to=&page=&pageSize=`
  (até 500 por página; `amount` negativo é débito em conta; `type` DEBIT/CREDIT; traz `category`,
  `categoryId`, `merchant` com nome/CNPJ/ramo, `paymentData`, `creditCardMetadata` com parcela, `billId`,
  `status` POSTED/PENDING), `GET /bills?accountId=`, `GET /investments?itemId=`. `PATCH /items/{id}` pede
  atualização.
- Webhooks existem (item/*, transactions/*), entregam até 3 vezes em ~2 horas. Fora deste plano: a API dorme
  no Render gratuito.

## 2. Decisões tomadas com o dono

| Decisão | Escolha |
| --- | --- |
| Credenciais | Cada pessoa cola as suas (Client ID, Client Secret, Item IDs). A API guarda cifrado por usuário e grupo. Fica dentro da regra "uso pessoal, só o titular". |
| Entrada das transações | Tudo passa pela revisão antes de entrar, com **Selecionar tudo** para confirmar num toque; a revisão destaca o que pode ser colisão com registro manual, de notificação ou de extrato. |
| Escopo de dados | Despesas, entradas como renda do mês, saldos e fatura do cartão, investimentos (patrimônio), histórico retroativo. Tudo guardado com riqueza suficiente para a IA. |
| Privacidade no grupo | Tudo o que uma pessoa conecta é do grupo: o parceiro vê contas, saldos, faturas e investimentos. Quem conectou é quem edita e desconecta. |
| Quem sai do grupo | Sair do grupo ou ser removido retira o Open Finance da pessoa daquele grupo: a conexão é desconectada, as credenciais são apagadas na hora e os bancos, contas e saldos dela deixam de existir no grupo (a partir da fase 2, também o espelho). Transações e rendas já confirmadas ficam. Ela pode ligar os mesmos Item IDs em outro grupo. Decidido em 07/10/2026; é a issue #31, entregue antes da fase 2. |
| Arquitetura | Camada própria `OpenFinance` na API com **espelho completo** dos dados do Pluggy; revisão e conciliação próprias; não reaproveita o job de importação de extrato. |

## 3. Modelo de dados

Contexto novo `CoupleSync.Application.OpenFinance`, entidades em `CoupleSync.Domain.Entities`, repositórios em
`CoupleSync.Infrastructure.Persistence`, gravação pelo `DbSaveTranslator`. Todas as tabelas implementam
`ICoupleScoped` e entram no filtro global por grupo. Migration aditiva, sem tocar em dado existente.

Mudança em tabela existente: `TransactionSource` ganha `OpenFinance = 3`. `TransactionEventIngest` ganha a
constante de banco `OPENFINANCE` ao lado de `MANUAL` e `OCR` (não vem do endpoint de notificação).

| Tabela / entidade | Campos |
| --- | --- |
| `bank_connections` / `BankConnection` | id, couple_id, user_id, provider (`PLUGGY`), label (apelido), client_id_encrypted, client_secret_encrypted, client_id_hint (4 últimos), status (`Active`, `Error`, `Disconnected`), last_sync_at_utc, last_error_code, last_error_message (pt-BR), history_months (3/6/12), created_at_utc, updated_at_utc. Única por (couple_id, user_id). |
| `bank_items` / `BankItem` | id, couple_id, connection_id, pluggy_item_id (único), connector_name (banco), status e execution_status como o Pluggy devolve, last_updated_at_utc, last_error_message, created_at_utc. |
| `bank_accounts` / `BankAccount` | id, couple_id, item_id, pluggy_account_id (único), type (`BANK`/`CREDIT`), subtype (`CHECKING_ACCOUNT`/`SAVINGS_ACCOUNT`/`CREDIT_CARD`), name, marketing_name, number_masked, currency, balance, balance_at_utc; cartão: credit_limit, available_credit_limit, balance_close_date, balance_due_date, minimum_payment, brand; sync_enabled (padrão true), updated_at_utc. |
| `bank_transactions` / `BankTransaction` (**espelho**) | id, couple_id, user_id, bank_account_id, pluggy_transaction_id (único), date (UTC) e local_date (dia no fuso do Brasil), amount (com sinal, como o Pluggy), type (`Debit`/`Credit`), description, description_raw, pluggy_category, pluggy_category_id, merchant_name, merchant_cnpj, merchant_category, payment_method, installment_number, installment_total, bill_id, status (`Posted`/`Pending`), balance_after, review_state, linked_transaction_id (FK `transactions`, nulo), linked_income_source_id (FK `income_sources`, nulo), matched_transaction_id (colisão suspeita, nulo), auto_reason (`Transfer`/`BillPayment`, nulo), suggested_category (chave do app), reviewed_at_utc, raw_json (`jsonb`), sync_run_id, created_at_utc, updated_at_utc. |
| `credit_card_bills` / `CreditCardBill` | id, couple_id, bank_account_id, pluggy_bill_id (único), due_date, total_amount, minimum_payment, is_closed, raw_json, updated_at_utc. |
| `investments` / `Investment` | id, couple_id, user_id, item_id, pluggy_investment_id (único), name, type, subtype, issuer, balance, amount (aplicado), rate, rate_type, due_date, currency, raw_json, updated_at_utc. |
| `balance_snapshots` / `BalanceSnapshot` | id, couple_id, kind (`Account`/`Investment`), ref_id, day (data), balance, currency. Única por (kind, ref_id, day); a sincronização do dia sobrescreve. |
| `sync_runs` / `SyncRun` | id, couple_id, connection_id, status (`Pending`, `Running`, `Done`, `Failed`), triggered_by (`User`/`AppOpen`/`Scheduler`), started_at_utc, finished_at_utc, transactions_new, transactions_updated, error_code, error_message, created_at_utc. Também é a fila do job. |

`review_state` de uma transação do espelho:

| Estado | Significado |
| --- | --- |
| `Pending` | Aguarda a pessoa. |
| `Confirmed` | Virou transação (`linked_transaction_id`) ou renda (`linked_income_source_id`). |
| `Reconciled` | Era a mesma de um registro manual, de notificação ou de extrato; `linked_transaction_id` aponta para ele. |
| `Discarded` | A pessoa descartou, ou apagou a transação criada a partir dela. Pode ser restaurada. |
| `Ignored` | Decidida automaticamente (`auto_reason`). Pode ser restaurada. |

Nada é apagado do espelho pela revisão. Apagar de verdade só pela ação "Apagar dados bancários importados" (seção 7).

Mapa de categorias do Pluggy para as 7 chaves do app: tabela estática `PluggyCategoryMap` em
`CoupleSync.Domain.ValueObjects`, chaveada pelo `categoryId` do Pluggy com reserva pelo nome, trancada por teste.
Sem correspondência: `OUTROS`; com consentimento de IA e descrição não vazia, `ICategoryClassifier` (Gemini) como
hoje na importação de extrato.

## 4. Cliente Pluggy

Interface `IPluggyClient` em `CoupleSync.Application.Common.Interfaces`; implementação `PluggyHttpClient` em
`CoupleSync.Infrastructure.Integrations.Pluggy` com `HttpClient` nomeado (timeout 30 s).

```
Task<PluggyAuth> AuthenticateAsync(string clientId, string clientSecret, CancellationToken ct);   // POST /auth
Task<PluggyItem> GetItemAsync(PluggyAuth auth, string itemId, CancellationToken ct);
Task<IReadOnlyList<PluggyAccount>> GetAccountsAsync(PluggyAuth auth, string itemId, CancellationToken ct);
Task<IReadOnlyList<PluggyTransaction>> GetTransactionsAsync(PluggyAuth auth, string accountId, DateOnly from, DateOnly to, CancellationToken ct); // pagina até o fim
Task<IReadOnlyList<PluggyBill>> GetBillsAsync(PluggyAuth auth, string accountId, CancellationToken ct);
Task<IReadOnlyList<PluggyInvestment>> GetInvestmentsAsync(PluggyAuth auth, string itemId, CancellationToken ct);
Task RequestItemUpdateAsync(PluggyAuth auth, string itemId, CancellationToken ct);               // PATCH /items/{id}
```

- A `apiKey` de cada conexão fica em cache de memória por 110 minutos (`IMemoryCache`, chave pelo id da conexão);
  401 invalida o cache e tenta uma vez de novo.
- Erros viram `PluggyException(code)` com códigos fechados: `PLUGGY_INVALID_CREDENTIALS`, `PLUGGY_ITEM_NOT_FOUND`,
  `PLUGGY_ITEM_NEEDS_ACTION`, `PLUGGY_UNAVAILABLE`, `PLUGGY_RATE_LIMITED`. A API traduz cada um numa mensagem em
  português no formato único `{code, message, errors}`.
- Datas e números lidos com cultura invariante; nada de `CultureInfo("pt-BR")`.
- Testes com `HttpMessageHandler` falso e respostas montadas a partir dos exemplos da documentação do Pluggy.
  Nenhum dado real.

## 5. Sincronização

`SyncConnectionService` (Application) executa uma `SyncRun`:

1. Autentica. Falha de credencial → conexão em `Error`, run `Failed`, mensagem orientando a conferir no Dashboard.
2. Para cada item: `GetItem` (atualiza status; item com erro de login ou aguardando ação → item marcado, run
   continua com os outros); `GetAccounts` → upsert em `bank_accounts` (contas novas nascem com `sync_enabled`).
3. Para cada conta com `sync_enabled`: janela `from` = última sincronização bem-sucedida da conexão menos 7 dias
   (ou hoje menos `history_months` na primeira vez) até hoje; `GetTransactions` paginado → upsert em
   `bank_transactions` por `pluggy_transaction_id`. Linha nova entra `Pending` e passa pela pré-classificação
   (seção 6). Linha existente atualiza só os campos vindos do Pluggy (status, descrição, categoria, saldo), nunca
   `review_state` nem vínculos. Para cartão: `GetBills` → upsert.
4. `GetInvestments` por item → upsert; `balance_snapshots` do dia para cada conta e investimento.
5. Fecha a run com contagens. `last_sync_at_utc` da conexão só avança em run `Done`.

Execução: `OpenFinanceSyncJob` (`BackgroundService`, mesmo padrão de `OcrBackgroundJob`): puxa `sync_runs`
`Pending` a cada 5 s, marca `Running`, executa, e ao subir falha as runs presas em `Running`. Runs são
serializadas por conexão.

Disparos:

- `POST /api/v1/openfinance/connections/{id}/sync` → cria run `Pending` (`User`), devolve 202 com o id da run.
  Com `force=true` chama `RequestItemUpdateAsync` antes (só no botão manual). Limite: uma run por conexão a cada
  10 minutos (`409 SYNC_TOO_SOON` com a hora da próxima).
- Ao abrir o app, se `last_sync_at_utc` da conexão do grupo tem mais de 6 horas, o app chama o mesmo endpoint
  (`AppOpen`); erro aqui é silencioso.
- `OpenFinanceDailyScheduler`: uma vez por dia (06:00 no horário de Brasília, pelo `BrazilTime`), enfileira uma
  run para cada conexão ativa cujo último sucesso tem mais de 20 horas. Só vale enquanto a API estiver acordada;
  a garantia real é o disparo ao abrir o app.
- `GET /api/v1/openfinance/sync-runs/{id}` para o app acompanhar.

## 6. Pré-classificação e revisão

Ao entrar uma linha nova no espelho, nesta ordem (a primeira regra que bate decide):

1. **Transferência própria**: outra linha no grupo com mesmo valor absoluto, sinal oposto, `local_date` igual, em
   conta diferente do grupo (qualquer pessoa) → as duas `Ignored` com `auto_reason = Transfer`.
2. **Pagamento de fatura**: débito em conta `BANK` cujo valor absoluto é igual ao `total_amount` de uma fatura
   fechada de um cartão do grupo com vencimento a até 10 dias, ou cuja descrição crua contém "PAGAMENTO" e
   "FATURA" → `Ignored`, `auto_reason = BillPayment`.
3. **Crédito vindo do parceiro**: `Credit` cujo contraparte (`paymentData.payer`, CPF mascarado ou nome) bate com o
   titular de outra conta do grupo → `Ignored`, `Transfer`.
4. **Colisão suspeita** (só débitos): existe `transactions` do grupo com `amount` igual ao valor absoluto,
   `event_timestamp_utc` no dia local igual ou a 1 dia de distância, `source` diferente de `OpenFinance`, e, se
   o `bank` da transação é um banco conhecido, igual ao `connector_name` → fica `Pending` com
   `matched_transaction_id`.
5. `suggested_category` pelo `PluggyCategoryMap`, com Gemini como reserva se houver consentimento.

Endpoints de revisão (todos filtram pelo grupo do token):

- `GET /api/v1/openfinance/review?month=AAAA-MM` → despesas pendentes, entradas pendentes, ignoradas e
  descartadas (recolhidas), com contagens e a lista de colisões.
- `POST /api/v1/openfinance/review/confirm` com `{ expenses: [{id, category?, description?}], incomes: [{id, name?}],
  discard: [id], reconcile: [{id, keepExisting: true}], asNew: [id] }`. Valor não é editável. Cria
  `Transaction` (`Source = OpenFinance`, `bank = connector_name`, `merchant = merchant_name ?? description`,
  `event_timestamp_utc = date`, categoria = escolhida ?? sugerida ?? `OUTROS`) e um `TransactionEventIngest` com
  banco `OPENFINANCE`; impressão digital = hash de `couple_id|OPENFINANCE|pluggy_transaction_id` pelo
  `IFingerprintGenerator` (reimportar nunca duplica). Entrada confirmada cria `IncomeSource` do mês da `local_date`,
  `isShared = true`, `isRecurring = false`. `reconcile` marca `Reconciled`, aponta para a existente e preenche
  nela `merchant` e `category` só se estiverem vazios / `OUTROS`. Resposta: criadas, conciliadas, descartadas.
- `POST /api/v1/openfinance/review/restore` com `[id]` → de `Discarded`/`Ignored` para `Pending`.
- Apagar uma `Transaction` com origem `OpenFinance` (rota existente de transações) devolve a linha do espelho
  a `Discarded` e limpa o vínculo.
- Linha `Pending` no banco (`status = Pending`) aparece, mas confirmar/conciliar devolve
  `422 TRANSACTION_NOT_POSTED`.

Tela **Revisão do banco** (`app/(main)/openfinance/review.tsx`, aba oculta cujo pai é Transações; estado zerado a
cada visita por `resetOnFocus`): seletor de mês; aviso no topo "N possíveis duplicadas precisam de você"; seções
Despesas, Entradas, Ignoradas e Descartadas (as duas últimas recolhidas, com "restaurar"); linhas agrupadas
por dia com estabelecimento, descrição, valor, categoria (chip editável) e marca de colisão com os botões
"É a mesma" e "É outra"; **Selecionar tudo** marca as despesas sem colisão; **Confirmar selecionadas** envia
em lotes de 200. Entradas têm a ação "Registrar como renda do mês". Lançamentos pendentes no banco ficam em
cinza e sem caixa.

## 7. Conexão: wizard e gestão

Endpoints:

- `POST /api/v1/openfinance/credentials/test` `{clientId, clientSecret}` → 200 "válidas" ou erro do Pluggy
  traduzido. Não grava nada.
- `POST /api/v1/openfinance/connections` `{label, clientId, clientSecret, historyMonths}` → cria a conexão
  (uma por pessoa por grupo; 409 se já existe).
- `POST /api/v1/openfinance/connections/{id}/items` `{itemId}` → verifica o item com as credenciais da conexão,
  grava `bank_items` e `bank_accounts`, devolve as contas encontradas. Lista vazia → `422 PLUGGY_ITEM_EMPTY`
  ("Nenhuma conta neste item. Confira se o conector MeuPluggy está ligado na aplicação e se a conexão foi feita
  pela Demo com a sua conta do Meu Pluggy.").
- `GET /api/v1/openfinance/status` → conexões do grupo (apelido, pessoa, status, hint do client id, última
  sincronização, erro), itens, contas com `sync_enabled`, se a funcionalidade está disponível no servidor.
- `PATCH /api/v1/openfinance/accounts/{id}` `{syncEnabled}`; `DELETE /api/v1/openfinance/connections/{id}`
  (só quem conectou) → apaga as credenciais e marca `Disconnected`; espelho e transações confirmadas ficam.
- `DELETE /api/v1/openfinance/data` (só quem conectou, para a própria conexão) → apaga espelho, faturas,
  investimentos e snapshots da conexão; transações e rendas já confirmadas ficam.

Wizard no app (`app/(main)/settings/openfinance/…`, aba oculta com pai Configurações; progresso guardado em
`SecureStore` por usuário, registrado em `userData.ts`):

1. **O que é**: texto em português explicando Meu Pluggy (gratuito, regulado, só os seus dados), o que o app
   passa a ver, que o parceiro vê tudo, e o aceite de privacidade específico (`privacyContent.ts`); a IA segue
   o consentimento já existente.
2. **Meu Pluggy**: botão "Abrir meu.pluggy.ai" (`Linking.openURL`); passos: criar conta → "Conectar conta" →
   escolher banco → autorizar no app do banco. Caixa "Conectei meus bancos".
3. **Dashboard**: botão "Abrir dashboard.pluggy.ai"; passos: criar conta com o mesmo e-mail → criar aplicação →
   aba "Aplicação" → copiar Client ID e Client Secret. Dois campos (secret oculto) e **Testar credenciais**.
4. **Item ID**: passos: na aplicação, "Conectores" → ligar "MeuPluggy" → "Ir para Demo" → "Conectar conta" →
   escolher "MeuPluggy" → entrar com a conta do Meu Pluggy → menu de três pontos → "Copiar Item ID". Um campo
   por banco, "Adicionar outro banco", **Verificar** mostra as contas encontradas.
5. **Período**: 3, 6 ou 12 meses; "Conectar e sincronizar" cria a conexão, grava os itens, dispara a run e
   mostra o progresso; termina em "Ver N transações para revisar".

Tela **Open Finance** em Configurações: status, bancos e contas com interruptor "sincronizar", **Sincronizar
agora**, **Adicionar banco** (volta ao passo 4), **Desconectar**. Na tela de privacidade: **Apagar dados
bancários importados** com confirmação em duas etapas.

## 8. Telas de Contas e Patrimônio (a base para IA foi para o plano de IA)

- **Contas** (`app/(main)/accounts/index.tsx`, aba oculta com pai Painel): por pessoa, contas com saldo e
  "atualizado há"; cartões com fatura atual (total, fechamento, vencimento, limite disponível) e anteriores.
  `GET /api/v1/openfinance/accounts`. Painel ganha "Faturas a vencer nos próximos 30 dias" (`DashboardResponse`
  recebe um campo novo opcional; app antigo ignora).
- **Patrimônio** (`app/(main)/wealth/index.tsx`): total do grupo e por pessoa, por tipo, lista de posições e
  gráfico de 6 meses pelos snapshots (saldos de conta + investimentos). `GET /api/v1/openfinance/wealth?months=6`.
- **Base para IA**: saiu deste plano. O resumo por grupo e período, a detecção de recorrências, o chat com
  contexto, os insights do mês e as dicas de investimento com dados de mercado foram absorvidos pelo desenho de
  IA (`.claude/specs/2026-10-08-ia-financeira-design.md`, issues #37–#48; a issue #29 foi fechada como
  absorvida). O Open Finance só entrega os dados; como eles entram no pacote de fatos da IA está na seção 1.3
  daquele desenho.

## 9. Segurança e regras da casa

- Client secret e client id cifrados com AES-256-GCM (`System.Security.Cryptography`, nonce por valor), chave de
  32 bytes em Base64 na variável `OPENFINANCE_ENCRYPTION_KEY` do Render, criada só pelo dono. Sem a variável:
  `GET /status` devolve `available = false` e o app mostra "Indisponível neste servidor"; nenhuma rota de
  escrita funciona (`503 OPENFINANCE_UNAVAILABLE`).
- A API nunca devolve o secret nem o client id inteiro. Logs não registram segredo, `raw_json` nem descrição.
- Isolamento: toda consulta filtra pelo grupo do token; conexão, item e conta pertencem ao grupo; só quem
  conectou edita, desconecta e apaga dados.
- Limites de taxa: `credentials/test` e `sync` em 5 por minuto por usuário (`RateLimiting`).
- Testes: unidade (pré-classificação, mapa de categorias, cifragem, cliente com handler falso), integração
  SQLite (rotas, isolamento por grupo, estados da revisão), PostgreSQL (jsonb, índices únicos, filtro global).
  Nenhum dado real; fixtures a partir dos exemplos da documentação do Pluggy. O que exige Pluggy de verdade vai
  para a issue de checkpoint do dono.
- App: só JavaScript, sem dependência nativa; sai por OTA e precisa funcionar no APK 1.1.0. Textos em pt-BR.
  Estado por usuário em `userData.ts`; trabalho assíncrono preso à época da sessão.

## 10. Fases (uma issue cada, nesta ordem)

| # | Issue | Conteúdo | Pronto quando |
| --- | --- | --- | --- |
| 1 | Conexão e credenciais | Tabelas `bank_connections`, `bank_items`, `bank_accounts`; cifragem; `IPluggyClient` com auth, item, contas; rotas de testar, criar conexão, verificar item, status, desconectar; wizard passos 1–4; tela Open Finance em Configurações; cartão "Depende de você" com a variável do Render | Pessoa conecta e vê as contas encontradas |
| 1b | Sair do grupo retira o Open Finance (issue #31) | Sair ou ser removido desconecta, apaga credenciais, itens e contas da pessoa no grupo; Item ID livre para outro grupo | Nada de quem saiu fica para trás |
| 2 | Sincronização e revisão de despesas | `bank_transactions`, `sync_runs`; job; retroativo; mapa de categorias; rotas de sync e revisão (sem conciliação); wizard passo 5; tela Revisão do banco com selecionar tudo, confirmar, descartar, categoria; disparo ao abrir o app | Despesas entram sem digitar |
| 3 | Conciliação | Regras 1–4 da seção 6; "É a mesma"/"É outra"; restaurar; apagar transação devolve ao espelho | Nada duplica |
| 4 | Entradas e Contas | Entradas como renda do mês; `credit_card_bills`; tela Contas; "Faturas a vencer" no Painel | Vê saldos e o que vai vencer |
| 5 | Investimentos e Patrimônio | `investments`, `balance_snapshots`; tela Patrimônio com evolução | Vê o patrimônio do grupo |
| 6 | Base para IA — absorvida | Saiu deste plano: ver o desenho de IA (`.claude/specs/2026-10-08-ia-financeira-design.md`, issues #37–#48; issue #29 fechada como absorvida) | — |
| 7 | Depois | Webhook do Pluggy. As dicas de investimento com dados de mercado e o agendamento com a API dormindo foram absorvidos pelo desenho de IA (`.claude/specs/2026-10-08-ia-financeira-design.md`, issues #37–#48) | |

Cada fase: migration aditiva, testes que falham antes e passam depois, esteira completa, OTA. A fase 1 só mostra
a tela como disponível depois que o dono criar `OPENFINANCE_ENCRYPTION_KEY` no Render.
