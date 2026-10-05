# Sessão 3 — Teste exploratório: orçamento, fontes de renda e notificações/alertas

- **Data e hora da execução registrada no log:** 05/10/2026, 16:20–16:25 (horário de Brasília) — 19:20:44Z a 19:25:33Z
- **Commit testado:** `98e3f64`
- **Alvo:** API CoupleSync em `http://localhost:5000/api/v1` (caixa-preta; código lido só em `Controllers` e `Contracts` para descobrir rotas e formatos)
- **Total de casos:** 105 (3.01 a 3.105), 301 requisições HTTP
- **Evidências:** `sessao-3.sh` (script reexecutável) e `sessao-3.log` (saída completa; identificador da execução `20261005192043-3745`)
- **Cenário criado pelo script:** casal X (A = Ana, B = Bruno e, depois, D = Davi como terceiro membro), casal Y (C = Carla), usuário E sem casal e três casais auxiliares de um só usuário para medir alertas sem interferência (V, T e W).
- **Consultas ao banco:** somente `SELECT` via `psql`, registradas no log.

Resumo dos status HTTP: 106×200, 66×201, 21×204, 54×400, 5×401, 7×403, 24×404, 4×405, 1×409, 2×415, 5×422 e **6×500**.

Observação sobre rotas: o registro de token de dispositivo fica em `POST /api/v1/devices/token` (e não sob `/notifications/`); `POST /api/v1/notifications/devices/token` responde 404 (caso 3.89). Não existe `GET /api/v1/incomes` (405, caso 3.62); a leitura é por `/incomes/current` e `/incomes/{mês}`.

---

## O que testei e funcionou

**Orçamento**
- 3.06, 3.07 — `GET /budgets/current` e `GET /budgets/{mês}` sem plano: 404 `BUDGET_PLAN_NOT_FOUND`.
- 3.08 — `PUT` de alocações em `planId` inexistente: 404.
- 3.09 — Renda rápida (`PATCH /budgets/income`) sem plano: cria o plano do mês atual com a renda informada.
- 3.10, 3.11 — `POST /budgets` atualiza o plano existente (mesmo id); renda rápida com plano existente altera a renda; qualquer membro do casal (B) consegue.
- 3.12, 3.13 — Renda rápida negativa e zero: 400, valor anterior preservado.
- 3.16 — `POST /budgets` com mês `2026-13`, `2026-00`, `abc`, vazio e `2026-1`: 400.
- 3.17 (parte) — Renda negativa, moeda ausente, corpo vazio e JSON malformado: 400.
- 3.18, 3.19 (parte) — Criar e ler plano do mês passado e do mês futuro: 200 com os dados corretos.
- 3.20, 3.28 — Definir alocações válidas: 200; `budgetGap` = renda − soma das alocações (5000 − 600 = 4400).
- 3.22 — Alocação negativa: 400.
- 3.25 — Mesma categoria duas vezes (idêntica ou `Lazer`/`LAZER`): 422 `BUDGET_ALLOCATION_DUPLICATE_CATEGORY`; alocações anteriores preservadas.
- 3.26 — Categoria vazia, só espaços, 65 caracteres, moeda ausente (400) e moeda diferente da do plano (422).
- 3.27 (parte) — Lista vazia limpa as alocações (200); chave ausente/`null`: 400; 21 itens: 422 `BUDGET_ALLOCATION_LIMIT`.

**Gasto x alocação**
- 3.29, 3.30, 3.31 — Com "Alimentação" = 100, o gasto mostrado em `GET /budgets/current` foi 50, 85 e 120 (restante 50, 15 e −20), inclusive com transação lançada por B.
- 3.36 — Transação do terceiro membro D entra no gasto do orçamento do casal (Transporte 0 → 40).
- 3.37 — Transação datada do mês passado não conta no mês atual e aparece no plano do mês passado (77).

**Alertas (com as configurações de notificação já gravadas — ver problemas)**
- 3.46 a 3.50 — Alerta de orçamento: nenhum aos 50%; 1 `BudgetWarning` ao cruzar 80%; não repete aos 90%; 1 `BudgetExceeded` ao cruzar 100%; não repete aos 130%.
- 3.51 — Categoria com acento ("Alimentação") gera alerta normalmente; salto de 0 para 120% gera só o alerta de estouro.
- 3.52 — Alerta de transação grande: gerado para 501 e 600; não gerado para 100, 300, 499 e 500 (limite observado: acima de R$ 500).
- 3.54, 3.55 — Gasto em 30 dias: nenhum alerta com 2.900; 1 alerta ao chegar a 3.050.
- 3.59, 3.60 — Configurações respeitadas: com tudo desligado, nenhum alerta novo; religando só "transação grande", uma transação de 5.000 gerou só `LargeTransaction` (nenhum alerta de gasto em 30 dias).

**Rendas**
- 3.63 a 3.66 — Criar renda pessoal (A, B, D) e compartilhada: 201.
- 3.71 — Editar renda própria (nome e valor; só valor; corpo `{}` sem alteração): 200.
- 3.73 (parte) — `PUT` com valor negativo, texto e nome de 65 caracteres: 400.
- 3.74 — B não consegue editar nem excluir a renda pessoal de A: 403 `INCOME_SOURCE_FORBIDDEN`.
- 3.75 — B e D editam a renda compartilhada criada por A: 200.
- 3.76 (parte), 3.77, 3.78 (parte) — `POST` com valor negativo, mês inválido (`2026-13`, `2026-00`, `abc`, vazio, ausente), nome vazio/ausente/65 caracteres, moeda ausente e corpo vazio: 400; nome duplicado no mesmo mês: 409 `INCOME_SOURCE_DUPLICATE`.
- 3.80, 3.84 — Excluir renda: 204; excluir de novo ou id inexistente: 404; a listagem e o total refletem a exclusão.
- 3.81 (parte) — `GET /incomes/{mês}` para mês atual, passado e futuro: 200 com os dados do mês.

**Notificações**
- 3.85 — `GET /notifications/settings`: padrão com os três alertas ligados.
- 3.86, 3.87 — `PUT` completo e parcial: 204, valores gravados; as configurações de B não mudam quando A altera as suas.
- 3.88 (parte) — Valor texto (`"sim"`), número (`1`), JSON malformado e lista: 400; sem corpo: 415; valores anteriores preservados.
- 3.89 — Token de dispositivo válido: 204, gravado no banco.
- 3.90 — Token vazio, só espaços, ausente e `null`: 400.
- 3.91 — Token com 5.000 e com 513 caracteres: 400 `INVALID_TOKEN`; com 512: 204.
- 3.92 (parte) — Mesmo token duas vezes pelo mesmo usuário: 204 e uma única linha no banco.
- 3.93 — Plataforma `ios`, vazia e ausente: 400; `ANDROID` maiúsculo aceito e gravado como `android`.

**Isolamento**
- 3.95 — C não vê o plano de X (nem por mês, nem passando o `planId`): 404.
- 3.96 — C não consegue trocar as alocações do plano de X: 404; plano de X intacto.
- 3.97 — Renda rápida de C cria/altera só o plano do casal Y; o de X continua com 5.000.
- 3.98 — C não vê rendas de X (listas vazias, total 0).
- 3.99 — C não consegue editar nem excluir rendas de X pelos ids: 404; valores conferidos no banco.
- 3.100 — Configurações de C independentes das de A.
- 3.101, 3.103 — Sem token ou com token adulterado: 401.
- 3.102 — Usuário sem casal: 403 `COUPLE_REQUIRED` em orçamento, rendas, configurações e token.

---

## O que testei e não funcionou — o que deve ser corrigido

| Caso | O que fiz | O que esperava | O que aconteceu (status + trecho) | Gravidade |
|---|---|---|---|---|
| 3.29–3.31, 3.41–3.44, 3.45 | Casal X com alocação "Alimentação" = 100; gastos chegaram a 85% e 120%. Nenhum membro tinha aberto a tela de configurações de notificação. Depois A abriu (`GET /notifications/settings`) e B e D lançaram gastos que cruzaram 80% e 100% de "Transporte". | Alertas de orçamento gerados, já que `GET /notifications/settings` mostra os três alertas ligados por padrão. | Nenhum evento de alerta em `notification_events` (só os dois `PartnerJoined`). A tabela `notification_settings` tinha 0 linhas para o casal; a linha só é criada no primeiro `GET`/`PUT` de configurações (3.45: 0 linhas antes do GET, 1 depois). Só houve alerta quando o **autor** da transação já tinha essa linha (3.44: A lança 600 → `LargeTransaction` + `BudgetExceeded`). Na prática, quem nunca abriu as configurações não recebe alerta nenhum. | Alta |
| 3.42–3.44 | No casal X (3 membros), verifiquei para quem são gravados os eventos de alerta. | O parceiro (ou todos os membros do casal) ser avisado de gasto grande e de orçamento estourado. | Os eventos são gravados só para quem lançou a transação (3.44: os dois alertas têm `destinatario = Ana S3`; B, que também tinha configurações gravadas e ligadas, não recebeu nada). | Média |
| 3.55–3.58 | Com 3.050 gastos em 30 dias (limite 3.000 já cruzado), lancei mais três transações de R$ 10. | Um alerta ao cruzar o limite, sem repetição. | **Um alerta novo por transação adicional**: 1 → 2 → 3 → 4 linhas `LowBalance` ("Your 30-day total spending of 3060.00 / 3070.00 / 3080.00 has exceeded the threshold."). | Alta |
| 3.47, 3.49, 3.52, 3.55 | Li os textos dos alertas gravados. | Textos em português, como o resto do aplicativo. | Só o aviso de 80% está em português ("Atenção: você usou 80% do orçamento de Lazer este mês."). Estão em inglês: `BudgetExceeded` ("Budget exceeded: Lazer" / "Spent 120.00 BRL of 100.00 BRL Lazer budget."), `LargeTransaction` ("Large transaction detected" / "A transaction of 501.00 BRL was detected.") e `LowBalance` ("High 30-day spending alert" / "Your 30-day total spending of ... has exceeded the threshold."). Além disso, o alerta de gasto alto em 30 dias usa o tipo e a configuração `LowBalance`/`lowBalanceEnabled` ("saldo baixo"). | Média |
| 3.32–3.35, 3.38 | Com alocação "Alimentação", lancei transações nas categorias `ALIMENTACAO`, `alimentação` e `Alimentacao`; com alocação "Outros", lancei em `OUTROS`. | Gasto somado na mesma alocação (ou a API recusar/normalizar a categoria). | Nenhuma variante foi somada: "Alimentação" ficou em 120 e "Outros" em 20 (os 30 de `OUTROS` não entraram). As seis grafias ficaram gravadas como categorias diferentes na tabela de transações. Ao gravar a alocação como `ALIMENTACAO`, ela mostra só os 10 dessa grafia (3.38). | Alta |
| 3.67–3.69 | A (5.000), B (3.000) e D (2.000, terceiro membro) criaram rendas pessoais; A criou uma compartilhada de 1.000 (11.000 no banco). Li `GET /incomes/current` como cada um. | Todos verem as rendas dos três e o mesmo total de 11.000. | A e B: `partnerIncome` mostra só o outro e `coupleTotal` = 9000.00 (a renda de D não aparece nem entra no total). D: `partnerIncome` = Ana (a renda de Bruno não aparece) e `coupleTotal` = 8000.00. O total do casal muda conforme quem consulta. | Alta |
| 3.72 | `PUT /incomes/{id}` com `{"name":""}` e com `{"name":"   "}`. | 400 com mensagem de validação. | **500** `{"code":"INTERNAL_SERVER_ERROR","message":"An unexpected error occurred."}` nos dois casos (o nome anterior foi preservado). | Alta |
| 3.14 | `PATCH /budgets/income` com `{"grossIncome":100000000000000000000}` (1e20). | 400. | **500** `INTERNAL_SERVER_ERROR`. | Média |
| 3.24 | `PUT /budgets/{planId}/allocations` com `allocatedAmount` = 1e20. | 400. | **500** `INTERNAL_SERVER_ERROR`. | Média |
| 3.73, 3.76 | `PUT /incomes/{id}` e `POST /incomes` com `amount` = 1e20. | 400. | **500** `INTERNAL_SERVER_ERROR` nos dois. | Média |
| 3.14, 3.24, 3.73, 3.76, 3.79 | Valor 9999999999999999.99 em renda rápida, alocação e fonte de renda. | Recusa por limite máximo plausível. | 200/201: valor gravado. O total de rendas do casal passou a `10000000000009040.99` e o `budgetGap` a `-9999999999994999.99`. Não há limite superior. | Média |
| 3.82, 3.83 | Criei renda com `isRecurring: true` no mês atual e consultei o mês seguinte; criei outra no mês passado e consultei o mês atual. | Renda recorrente aparecer nos meses seguintes. | Não aparece: `GET /incomes/2026-11` volta vazio (total 0) e a renda recorrente de 2026-09 não aparece em 2026-10. O campo é gravado, mas não tem efeito na leitura. | Média |
| 3.76, 3.78 | `POST /incomes` sem o campo `isRecurring`. | Padrão "não recorrente" (ou campo obrigatório). | A renda é criada com `"isRecurring":true`. | Baixa |
| 3.78, 3.79 | `POST /incomes` com moeda `XYZ` e `USD`. | Recusar moeda inexistente; não somar moedas diferentes. | 201 nos dois; os valores em `XYZ` e `USD` entram no total do casal, que é devolvido como `"currency":"BRL"`. | Média |
| 3.15, 3.17, 3.40 | Renda rápida e `POST /budgets` com moeda `XYZ`; renda rápida com `USD` em plano que já tinha alocações em BRL. | Recusar moeda inexistente; não deixar o plano com moeda diferente das alocações. | 200: plano gravado com `"currency":"XYZ"`. No 3.40 o plano ficou `"currency":"USD"` com as três alocações em `"currency":"BRL"` (a própria API recusa isso no `PUT` de alocações com `BUDGET_ALLOCATION_CURRENCY_MISMATCH`). | Média |
| 3.39 (também 3.37, 3.38) | `PUT /budgets/{planId}/allocations` em plano que já tinha gastos e, logo depois, `GET /budgets/current`. | A resposta do `PUT` trazer o mesmo gasto do `GET`. | O `PUT` responde `"actualSpent":0` e `"remaining"` cheio para todas as alocações; o `GET` seguinte mostra 120, 20 e 40. A tela que usar a resposta do `PUT` mostra gasto zerado. | Média |
| 3.21 | Alocações somando 6.000 com renda de 5.000. | Recusa ou aviso explícito. | 200, gravado; o único sinal é `"budgetGap":-1000.00`. | Baixa |
| 3.13 x 3.17 | Renda zero pela renda rápida e pelo `POST /budgets`. | Mesma regra nos dois caminhos. | Renda rápida: 400 "GrossIncome must be greater than zero."; `POST /budgets` com `grossIncome: 0`: 200. | Baixa |
| 3.23, 3.73, 3.76 | Alocação com valor zero; renda com valor zero (criar e editar). | Recusar valor zero. | 200/201, gravado. | Baixa |
| 3.15, 3.76, 3.79 | Valores com três casas decimais (`1234.567`, `10.999`). | Recusa ou resposta igual ao que foi gravado. | A resposta devolve `1234.567` e `10.999`; no banco fica arredondado (a renda de 10.999 aparece depois como `11.00`). | Baixa |
| 3.18 | `POST /budgets` para os meses `1900-01` e `9999-12`. | Recusar meses fora de um intervalo plausível. | 200, planos criados. | Baixa |
| 3.19 | `GET /budgets/2026-13`, `/2026-00` e `/abc`. | 400 (formato de mês inválido), como no `POST`. | 404 "No budget plan for month 'abc'." — indistinguível de "mês sem plano". | Baixa |
| 3.81 | `GET /incomes/2026-13`, `/2026-00` e `/abc`. | 400. | 200 com `"month":"abc"` e listas vazias. | Baixa |
| 3.78 | `POST /incomes` com nome `salario a` existindo `Salario A` no mesmo mês. | 409, como no nome idêntico. | 201: a duplicidade só é detectada com a mesma caixa. | Baixa |
| 3.92 | Registrei o mesmo token de dispositivo para A, para B (mesmo casal) e para C (outro casal). | O token passar a pertencer só ao último usuário que o registrou. | 204 nos três; o banco fica com três linhas com o mesmo token, uma por usuário, em dois casais. Um aparelho usado por duas contas receberia notificações das duas. | Média |
| 3.88 | `PUT /notifications/settings` com `{"campoInexistente":true}` e com os três campos `null`. | 400. | 204, nada alterado. | Baixa |
| 3.47–3.61, 3.105 | Acompanhei o `status` dos eventos de notificação. | Entrega ou, sem aparelho registrado, um estado que não seja falha. | Todos os eventos passam de `Pending` para `Failed` em poucos segundos, com `delivered_at_utc` vazio. Neste ambiente nenhum usuário desses casais tinha token real; registro como observação. | Baixa |
| 3.12–3.17, 3.22, 3.26, 3.73, 3.76–3.78, 3.88, 3.104 e todos os erros | Registrei os formatos de corpo de erro. | Um único formato, em português, sem detalhe interno. | Quatro formatos: (a) `{"code","message","traceId"}`; (b) `{"code","message"}` sem `traceId` (404 de `GET /budgets/...`, `INVALID_TOKEN`, `UNSUPPORTED_PLATFORM`); (c) ProblemDetails `{"type","title","status","errors","traceId"}` nas validações e no 415; (d) corpo vazio em 401, 405 e 404 de rota ou id não-GUID. **Todas as mensagens estão em inglês.** Vazam nomes internos: "The JSON value could not be converted to CoupleSync.Api.Contracts.Budget.UpdateIncomeRequest. Path: $.grossIncome \| LineNumber: 0 \| BytePositionInLine: 19." (também `...Contracts.Income.CreateIncomeSourceRequest`, `...Controllers.UpdateNotificationSettingsRequest`) e "The request field is required.". | Média |
| 3.03, 3.05, 3.41 | Conferi os eventos gerados quando B e D entraram no casal X. | Todos os membros já existentes serem avisados da entrada de D. | Só A recebeu `PartnerJoined` nas duas entradas; B não foi avisado da entrada de D. (Fora do escopo principal; registrado porque apareceu na consulta.) | Baixa |

---

## Funcionalidade não testada (faltou ou não foi implementada)

- **Leitura de notificações/alertas pela API:** não existe rota para listar, marcar como lida ou apagar alertas (`GET /notifications`, `/notifications/events` e `/alerts` → 404, caso 3.94). Os alertas só puderam ser conferidos pelo banco.
- **Remoção de token de dispositivo (logout):** não existe rota (`DELETE /devices/token` → 405, caso 3.94).
- **Entrega real de push:** não testada; não há aparelho nem credencial de envio no ambiente (todos os eventos terminaram como `Failed`).
- **Alerta de lembrete de conta (`billReminderEnabled`):** a configuração existe e é gravada, mas não encontrei ação na API, dentro do escopo, que gere esse tipo de alerta; nenhum evento desse tipo apareceu.
- **Alerta de saldo baixo propriamente dito:** não há conceito de saldo nas rotas testadas; `lowBalanceEnabled` controla, na prática, o alerta de gasto acima de R$ 3.000 em 30 dias.
- **Exclusão de plano de orçamento e de uma alocação isolada:** não existem rotas (`DELETE /budgets/{id}` → 405, caso 3.104); alocações só são trocadas em bloco.
- **Listagem de todos os planos do casal:** não existe (`GET /budgets` → 405).
- **Alertas a partir de transações que não são manuais** (importação por notificação bancária, OCR): não testado; só usei `POST /transactions`.
- **Janela de 30 dias do alerta de gasto:** não testei transações com data antiga saindo da janela, nem a virada de mês para os alertas de orçamento.
- **Plataforma iOS para token:** recusada por projeto ("Only 'android' platform is supported."); nada a testar além da recusa.
- **Concorrência** (dois membros alterando alocações ou renda ao mesmo tempo) e **expiração do token de acesso** durante o uso: não testadas.
- **Rendas do terceiro membro em outros totais** (painel, relatórios, fluxo de caixa): fora do escopo desta sessão.
