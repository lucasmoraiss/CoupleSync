# CoupleSync simples — redesenho de produto, categorização e navegação

Data: 2026-10-16. Decidido com o dono no mesmo dia (seção 15). Este documento substitui,
no que conflita, a parte de telas dos desenhos de Open Finance (`.claude/specs/2026-10-07-open-finance-design.md`)
e de IA (`.claude/specs/2026-10-08-ia-financeira-design.md`); as regras de provedor, cota, privacidade e cadeia
de IA daquele documento continuam valendo. Cada fase da seção 13 vira uma issue com o rótulo `backlog` e passa
pela esteira `/entregar`.

Legenda: **[código]** = fato do repositório em `origin/main` (`c2eb441`), com caminho; **[externo]** = fato de fonte
externa, verificado em 16/10/2026; **minha escolha** = decisão de desenho tomada por mim, que o dono pode trocar;
**dono** = decisão que só ele toma (seção 15).

## 1. Por que

O dono usa o app com o banco conectado pelo Open Finance e a IA ligada, e o app não o ajuda. Palavras dele
(16/10/2026): não tem "boa visibilidade e clareza do que estou gastando"; "toda transação nova eu tenho que
validar", o que "não deveria ser obrigatório"; "90% do meu orçamento está em 'Outros' e isso não me diz NADA";
o app está "confuso, complexo e pesado", com "muitas funções que muitas vezes são até sem propósito (noto isso na
quantidade de abas disponíveis)"; "fizemos muita coisa no passado na emoção". Ele quer um app "simples, útil e
trivial", tomando por modelo os aplicativos pagos e maduros de orçamento, "pode ser um clone completo", com um
diferencial: **uma visão só minha e uma visão do casal ou da família, finanças compartilhadas de fato**.

O reconhecimento do código (16/10/2026) explica cada queixa:

| Queixa | Causa no código [código] |
|---|---|
| 90% em "Outros" | Só 7 categorias (`TransactionCategories`: ALIMENTACAO, TRANSPORTE, COMPRAS, SAUDE, LAZER, MORADIA, OUTROS). O `PluggyCategoryMap` achata as ~90 categorias do Pluggy nessas 7 e manda para OUTROS tudo o que não é gasto de consumo: transferências, Pix para pessoa, pagamento de fatura, investimentos, tarifas, impostos, educação, assinaturas, pets, cuidados pessoais. O classificador de IA é o `NullCategoryClassifier` (`DependencyInjection.cs:114-121`). Corrigir uma categoria não ensina nada. |
| Validar toda transação | Linha do banco nasce `BankTransactionReviewState.Pending` e só vira `Transaction` depois de "Confirmar" em `openfinance/review.tsx`. O que não é confirmado não conta em Painel, Relatórios, Fluxo, orçamento nem alertas. Linha `Pending` no banco não pode nem ser confirmada. |
| Sem visibilidade | O Painel mostra total do mês, por pessoa e por categoria. Não mostra renda, saldo, orçamento, previsão, nem o que mudou em relação ao mês anterior. O orçamento por categoria existe inteiro na API (`/api/v1/budgets`, alertas de 80% e estouro) e **nenhuma tela o usa**. |
| Confuso e pesado | 7 abas (Painel, Transações, Metas, Fluxo, Rendas, Relatórios, Config) + 15 telas ocultas. "Rendas" é uma aba inteira para cadastrar fontes de renda. "Fluxo" é um número. Cada tela tem seu próprio StyleSheet: não há Card, linha, botão, chip nem seletor de categoria reutilizáveis (`mobile/src/components/`). Só tema escuro fixo. |
| Sem visão pessoal × casal | `Transaction` não tem marca de pessoal/compartilhada; tudo é do grupo. A única noção de compartilhado é `IncomeSource.IsShared`. Qualquer membro vê e edita qualquer transação. |

## 2. Propósito, princípios, não-objetivos

### 2.1 Propósito

Responder em menos de dez segundos, sem nenhuma tarefa prévia: **quanto gastei este mês, com o quê, quanto ainda
posso gastar, e isso é meu ou é nosso**. Tudo o mais é secundário e fica a um toque de distância ou fora do app.

### 2.2 Princípios (valem para todas as fases)

1. **Nada bloqueia o número.** Transação do banco entra, é categorizada e conta no mesmo instante da
   sincronização. Revisar é opcional, sempre; nunca há fila obrigatória entre o banco e a tela.
2. **O app categoriza; a pessoa corrige quando quiser, e o app aprende.** Regra determinística antes de IA; IA
   antes de "Outros"; correção vira regra do grupo. Meta medida: menos de 10% do valor gasto do mês em "Outros"
   nos dados reais do dono (hoje ~90%).
3. **Quatro abas.** Início, Lançamentos, Orçamento, Mais. Nenhuma funcionalidade nova ganha aba.
4. **Uma troca, duas visões.** Um seletor **Eu | Nós** no topo das três telas principais muda todos os números
   da tela. Mesmo layout, mesma hierarquia, só o recorte muda.
5. **Modelo, não invenção.** Padrões de tela e de interação copiados do Copilot Money (navegação, inbox de
   revisão opcional, categorização com aprendizado) e do Monarch Money (compartilhamento de casal, regras por
   estabelecimento, orçamento por categoria com restante) [externo: getfinny.app e useorigin.com, out/2026].
   Onde eles divergem, prevalece o mais simples.
6. **Subtrair antes de somar.** Toda tela nova nasce com menos elementos do que a que substitui. Funcionalidade
   que não serve ao propósito da seção 2.1 sai da navegação principal; o código de API dela fica, sem tela ou
   em "Mais".
7. **Um sistema visual.** Componentes compartilhados, tokens de cor por categoria, tipografia com algarismos
   tabulares, tema claro e escuro pelo sistema. Nenhuma tela define cor fora dos tokens.
8. **Compatível.** Mudanças de API só aditivas até a fase 7; migrations só aditivas; o APK 1.3.0 continua
   funcionando enquanto o OTA não chega; `docs/` (trabalho da faculdade) não é tocado.

### 2.3 Não-objetivos e anti-objetivos

Não-objetivos (fora deste desenho, podem voltar depois):
- Dividir despesas e calcular "quem deve a quem" (Splitwise). O "Nós" mostra quem pagou o quê; não cobra.
- Orçamento base zero, envelopes, sobra que rola para o mês seguinte (YNAB).
- Fontes de dados novas, iOS, web, mais de um idioma ou moeda.
- Remover código de API das funcionalidades rebaixadas (Metas, Fluxo, Relatórios, captura de notificação).
- As fases 4 a 9 do desenho de IA (insights, e-mail, educação, mercado): ficam como estão no backlog, e a
  fase 7 (insight diário) ganha um lugar previsto no Início (seção 7.1).

Anti-objetivos (se acontecer, o trabalho falhou mesmo "funcionando"):
- Mesma arquitetura de informação com tinta nova: sete abas viradas em quatro por agrupamento, com as mesmas
  telas dentro.
- Qualquer passo obrigatório entre sincronizar e ver o número.
- "Outros" continuar sendo a maior categoria do mês do dono.
- Tela do Início que precise rolar duas vezes para achar quanto se gastou.
- A visão "Nós" virar uma lista de dois nomes com dois totais; ela precisa ter orçamento e lançamentos próprios.

## 3. Modelo de referência

Síntese do que os apps de referência fazem, para o implementador copiar com segurança [externo]:

| Elemento | Copilot Money | Monarch Money | Escolha aqui |
|---|---|---|---|
| Abas | 4 (Dashboard, Transactions, Budget, Accounts) | 6+ (Dashboard, Accounts, Transactions, Budget, Recurring, Goals, Reports) | **4**: Início, Lançamentos, Orçamento, Mais (Contas vai em Mais, seção 5) |
| Revisão | Inbox "To review" opcional; tudo já conta | Opcional; "needs review" só quando uma regra marca | Inbox opcional "A revisar" no Início (seção 7.1), só duplicadas prováveis, categoria incerta e pendentes do banco |
| Categorização | Automática, aprende com correções ("Copilot Intelligence") | Regras por estabelecimento, criadas de uma edição ("apply to all") | Cascata regra do grupo → Pluggy → dicionário → IA em lote → Outros; correção vira regra (seção 6) |
| Casal | Um login compartilhado (fraco) | "Household": dois logins, tudo compartilhado, sem privacidade por conta | **Diferencial**: Eu | Nós com marca por lançamento, padrão por conta, privacidade do pessoal (seção 4) |
| Orçamento | Por categoria, anel de progresso, "restante" | Por categoria e grupo, "left to spend", sugestão pela média | Por categoria com barra, restante e "pode gastar por dia"; sugestão pela média de 3 meses (seção 7.3) |
| Transferências | Fora dos gastos, ocultas por padrão | Fora dos gastos, categoria própria | Tipo `Transferencia` fora dos gastos, ocultas por padrão com filtro "mostrar" (seção 6.1) |

## 4. Modelo de dados: tipo, escopo e categoria

### 4.1 Tipo do lançamento (`Kind`)

Novo campo em `Transaction` [código: `backend/src/CoupleSync.Domain/Entities/Transaction.cs`], enum
`TransactionKind { Despesa = 1, Receita = 2, Transferencia = 3, PagamentoFatura = 4, Investimento = 5 }`.
- Só `Despesa` conta como gasto (Início, Orçamento, Relatórios, alertas). `Receita` conta como renda do mês
  ao lado das fontes cadastradas (seção 7.3). Os outros três aparecem em Lançamentos sob o filtro "Mostrar
  transferências" e em nenhum total.
- Hoje toda `Transaction` é despesa e `Amount > 0`: a migration preenche `Kind = Despesa` em tudo o que existe.
  `Amount` continua positivo; o sinal vem do `Kind`.
- Origem do `Kind` no Open Finance: `BankTransaction.Type` (`Credit` → Receita, salvo quando a categoria
  Pluggy diz transferência/resgate) e a categoria Pluggy (famílias "Same person transfer", "Transfers",
  "Third-party transfers", "Credit card payment" → `Transferencia`/`PagamentoFatura`; "Investments" →
  `Investimento`; "Income" → `Receita`) [externo: docs.pluggy.ai/docs/transaction-categories]. A
  pré-classificação que hoje ignora transferências internas e pagamento de fatura
  (`BankPreclassificationService`) passa a gravar o `Kind` em vez de descartar.
- Manual: um seletor Despesa/Receita no formulário (padrão Despesa). Extrato PDF: linhas "Entrada" (hoje
  recusadas) entram como `Receita`.

### 4.2 Escopo: a pessoa é a unidade; o círculo vê o que cada um permite

Decisão do dono (16/10/2026, seção 15): o orçamento é **individual, por pessoa**; a visão do casal ou da
família é "mais simples e direta" (um resumo, com detalhe por baixo); se o outro quiser ver mais dos
lançamentos e do extrato de alguém, "deve haver a possibilidade", definida numa tela de configurações do
casal/círculo/família, "tudo parametrizável e customizável". Daqui em diante o documento chama o `Couple` de
**círculo** (casal ou família); na interface aparece o nome do grupo (issue #5) e o código não muda de nome.

Modelo:
- `Transaction` ganha `IsShared` (bool). `UserId` continua sendo quem pagou/registrou. `BankAccount` ganha
  `Scope { Pessoal, Compartilhada }`, **padrão Pessoal** sempre (decisão 2): nada é da casa até a pessoa
  dizer.
- **Eu** = meus lançamentos (`UserId` = eu), compartilhados ou não, de contas Pessoais, manuais ou de
  notificação. Meu orçamento = `BudgetPlan.UserId` = eu. Minha renda = minhas fontes + receitas das minhas
  contas. **Funciona sem ninguém mais no círculo.**
- **Nós** = a visão do círculo, em dois níveis:
  - *Resumo* (sempre): total dos lançamentos `IsShared = true` de todos os membros no mês, por categoria;
    orçamento compartilhado opcional (`BudgetPlan.UserId = null`); e, por membro, o que ele permitiu mostrar
    (abaixo). Linhas por membro: "Lucas · R$ 2.100 · 61% do compartilhado".
  - *Detalhe* (ao tocar num membro): os lançamentos, contas e saldos daquele membro **só até o nível que ele
    liberou**. Quem não liberou nada aparece só com o que compartilhou.
- **Conta conjunta**: conta marcada `Compartilhada` é dinheiro da casa, não de quem a conectou. Seus
  lançamentos nascem `IsShared = true`, **não entram no Eu de ninguém** e, no resumo por membro, aparecem
  como a conta ("Conta conjunta Itaú"). `UserId` fica como quem conectou, só para permissão de edição da
  conexão [código: `BankConnection`, 1 por pessoa e grupo].
- Um lançamento meu e compartilhado aparece nas duas visões: aluguel que eu paguei é meu gasto e é gasto da
  casa.

**Configurações do círculo** (tela nova em Mais → Grupo, seção 7.4; cada membro define só o que é dele e vê o
que os outros definiram):
1. *O que os outros veem de mim*: (a) **só o que eu compartilho** (padrão); (b) também meus totais por
   categoria; (c) tudo: meus lançamentos, contas e saldos. Vale para todos os outros membros do círculo (sem
   permissão por pessoa; minha escolha, para caber numa tela).
2. *Minhas contas compartilhadas*: a lista das minhas contas conectadas com um interruptor cada ("É da casa").
3. *Compartilhar por padrão*: categorias cujos lançamentos meus nascem compartilhados. Pré-marcadas (minha
   escolha, editáveis): Moradia, Contas da casa, Mercado. Vazio = nada nasce compartilhado.
4. *Orçamento compartilhado*: ligar ou não o plano do Nós (desligado por padrão; ligado, qualquer membro edita).
Ao **entrar num círculo** com outra pessoa (criar o segundo membro ou aceitar o convite), uma folha de
configuração inicial mostra esses quatro itens com os padrões e um botão "Pronto"; dá para pular e tudo fica
no padrão (nada visível além do compartilhado).

Regras derivadas:
- **Regra aprendida de escopo**: marcar um lançamento como compartilhado/pessoal com "aplicar aos próximos de
  [estabelecimento]" cria regra do grupo (seção 6.3, coluna `IsShared?`).
- **API**: `GET /transactions`, `/dashboard`, `/budgets`, `/openfinance` (contas e saldos) passam a filtrar
  pelo autor e pelo nível de visibilidade que o autor liberou para quem pergunta. Editar e excluir lançamento
  de outro membro deixa de ser permitido [código: hoje qualquer membro edita qualquer transação]; lançamento
  compartilhado é editável só pelo autor e pela categoria/escopo por qualquer membro (minha escolha).
- **Usar sozinho**: o registro cria um círculo de uma pessoa automaticamente (hoje a pessoa é obrigada a
  "criar ou entrar em grupo" antes de ver qualquer tela, `couple-setup.tsx` [código]); convidar alguém o
  transforma em casal sem mover dados. Entrar num círculo de outra pessoa continua como hoje (vira um segundo
  grupo; o pessoal fica acessível pela troca de grupo). Minha escolha, consequência direta de "orçamento
  individual".
- **Migração**: lançamentos existentes viram `IsShared = true` e as contas existentes `Compartilhada`
  (preserva o que os casais de hoje veem); visibilidade de todos nasce em (a). O grupo de hoje do dono não
  muda de comportamento até ele mexer nas configurações do círculo.

### 4.3 Taxonomia de categorias v2

Dois níveis: **grupo** (o que aparece no Orçamento e no Início) e **subcategoria** (opcional, no detalhe e nos
filtros). Chaves ASCII maiúsculas como hoje; rótulo, ícone Ionicons e cor fixos por grupo, servidos por
`GET /api/v1/categories` (hoje já existe, ganha `parent`, `icon`, `color`, `kind`). A lista é global, não por
grupo (categoria personalizada é não-objetivo).

Despesa (16 grupos):

| Chave | Rótulo | Subcategorias | Mapeia do Pluggy |
|---|---|---|---|
| MORADIA | Moradia | Aluguel ou financiamento, Condomínio, IPTU, Casa e decoração, Seguro residencial | Housing, Financing/Real estate, Insurance/Home |
| CONTAS_CASA | Contas da casa | Luz, Água, Gás, Internet, Celular, TV | Utilities, Services/Telecommunications |
| MERCADO | Mercado | Supermercado, Hortifruti e padaria | Groceries |
| RESTAURANTES | Restaurantes e delivery | Restaurante, Delivery, Bar e café | Food and drinks |
| TRANSPORTE | Transporte | Combustível, App de transporte, Transporte público, Estacionamento e pedágio, Manutenção, Seguro e taxas do carro, Financiamento do carro | Transportation, Automotive, Insurance/Vehicle, Financing/Vehicle |
| SAUDE | Saúde | Farmácia, Consultas e exames, Plano de saúde, Dentista, Ótica | Healthcare, Insurance/Health |
| BEM_ESTAR | Bem-estar e beleza | Academia, Esporte, Cabelo e beleza | Services/Wellness and fitness |
| COMPRAS | Compras | Roupas, Eletrônicos, Casa, Presentes, Livros e papelaria, Esporte, Compras online | Shopping (menos Pets e Kids) |
| FILHOS | Filhos e família | Escola e creche, Brinquedos, Pensão | Education/School, Kindergarten; Shopping/Kids; Legal/Alimony |
| PETS | Pets | — | Shopping/Pet supplies and vet |
| EDUCACAO | Educação | Cursos, Faculdade | Education/Online Courses, University; Financing/Student loan |
| LAZER | Lazer e viagem | Cinema e shows, Viagem, Hospedagem, Hobbies, Apostas | Leisure, Tickets, Travel, Gambling |
| ASSINATURAS | Assinaturas e serviços digitais | Streaming de vídeo, Música, Jogos, Software e apps | Digital services |
| TAXAS | Taxas, juros e impostos | Tarifa bancária, Juros e multa, Impostos, IOF | Bank fees, Taxes, Loans/Late payment, Interests |
| DOACOES | Doações | — | Donations |
| OUTROS | Outros | — | tudo o que não mapeia |

Receita (grupo RECEITA, usado só quando `Kind = Receita`): Salário, Renda extra, Rendimentos e dividendos,
Reembolso, Outras receitas — mapeia Income/* e Investments/Proceeds.
Transferência (`Kind` ≠ Despesa/Receita, sem orçamento): Entre minhas contas, Pix ou TED para pessoa,
Pagamento de fatura, Aplicação ou resgate, Empréstimo.

Migração das 7 chaves atuais: TRANSPORTE, SAUDE, COMPRAS, LAZER, MORADIA mantêm a chave e ganham o novo
rótulo; ALIMENTACAO vira MERCADO ou RESTAURANTES pela cascata da seção 6.2 (estabelecimento e categoria
Pluggy guardada em `BankTransaction.PluggyCategoryId` [código]), com MERCADO como padrão quando nada decide;
OUTROS é **recategorizado em lote** (seção 6.5). `ALIMENTACAO` continua aceita como alias de entrada enquanto
o APK 1.3.0 a enviar: `NormalizeOrOther` a resolve pela mesma regra em vez de jogar em OUTROS. As cópias
travadas por teste (`mobile/src/modules/transactions/categories.ts` e os testes de paridade app × API) são
atualizadas juntas.

### 4.4 Orçamento por visão

`BudgetPlan` [código: por grupo e mês] ganha `UserId?`: nulo = orçamento do "Nós"; preenchido = orçamento
pessoal daquele membro. Alocação por grupo de categoria (chave do grupo); subcategoria não tem limite próprio.
`GrossIncome` deixa de ser digitado: a renda do mês vem de `IncomeSource` + lançamentos `Receita` (seção 7.3),
e o campo passa a ser calculado (mantido na API como leitura para compatibilidade).

## 5. Navegação

```
┌──────────────────────────────────────────────────────────────┐
│  [Eu | Nós]            outubro ▾                 (grupo ▾) ⚙ │
│ ──────────────────────────────────────────────────────────── │
│                                                              │
│                        conteúdo da aba                       │
│                                                              │
│ ──────────────────────────────────────────────────────────── │
│   ◉ Início      ≡ Lançamentos      ◔ Orçamento     ⋯ Mais    │
└──────────────────────────────────────────────────────────────┘
```

| Hoje (7 abas) | Depois |
|---|---|
| Painel | **Início** (reescrito, seção 7.1) |
| Transações | **Lançamentos** (reescrito, seção 7.2) |
| Rendas | dentro de **Orçamento** (seção 7.3) |
| — (orçamento sem tela) | **Orçamento** (novo, seção 7.3) |
| Fluxo | uma linha no Início ("Previsão para o fim do mês"); a tela sai |
| Relatórios | Mais → Relatórios (mantida, retocada com os tokens novos) |
| Metas | Mais → Metas (mantida, retocada) |
| Config | Mais (seção 7.4) |
| Assistente (oculta) | Mais → Assistente, e atalho no Início quando a IA está disponível |
| Revisão do banco (oculta) | **A revisar** no Início (seção 7.1); a tela de revisão atual sai |
| Revisão de importação (oculta) | "Conferir e importar" (seção 6.4); acessível por Lançamentos → ⋯ → Importar extrato |
| Open Finance (oculta em Config) | Mais → Contas e bancos |

Cabeçalho comum das três primeiras abas: seletor **Eu | Nós** (segmented control; "Nós" mostra o nome do grupo
quando existir, issue #5; sem grupo com dois membros o seletor não aparece), seletor de mês (padrão mês
corrente de Brasília), troca de grupo só quando há mais de um (comportamento atual do `GroupSwitcher`). O
escopo escolhido persiste por aparelho (`zustand` + storage) e vale nas três abas ao mesmo tempo.

Abas ocultas do expo-router ficam montadas entre visitas [código: `resetOnFocus`, armadilha 2 do CLAUDE.md]:
toda tela nova reinicia estado por visita pelo mecanismo existente.

## 6. Categorização: cascata, aprendizado e revisão opcional

### 6.1 Ponto único de decisão

Um serviço `CategorizationPipeline` na camada Application, chamado por todas as portas de entrada (Open
Finance, extrato PDF, notificação, manual-sem-categoria), devolve `{ Kind, Category, Subcategory?, IsShared?,
Confidence, Source }`. `Source ∈ { RegraGrupo, Pluggy, Dicionario, IA, Padrao }`. Nunca lança; na falha de um
elo passa ao próximo; no fim devolve `OUTROS` com `Confidence = Baixa`.

### 6.2 Ordem (determinístico antes de IA)

0. **Tipo** (seção 4.1): decide `Kind` pela categoria Pluggy, pelo sentido (crédito/débito) e por padrões de
   texto conhecidos ("PAGAMENTO FATURA", "PIX ENVIADO" para CPF igual ao de outra conta do grupo, "APLICACAO",
   "RESGATE"). Só `Despesa` segue para categoria de gasto.
1. **Regra do grupo** (seção 6.3): `MerchantKey.Normalize(merchant)` [código: já existe pela fase 3 de IA] →
   categoria/subcategoria/escopo. Última correção vence.
2. **Pluggy**: `PluggyCategoryId` completo → taxonomia v2 pela tabela da seção 4.3 (prefixos de 2/4/6/8
   dígitos, como o `TryMap` atual, mas sem achatar). Confiança Alta.
3. **Dicionário global**: as 38 regras de `category-rules.json` crescem para ~300 estabelecimentos brasileiros
   (redes de supermercado, farmácias, postos, apps, streamings, operadoras, concessionárias de energia/água
   por estado) com subcategoria. Casamento por `MerchantKey` e por palavra inteira, não substring solta
   (hoje "99" casa com qualquer coisa que contenha 99). Confiança Alta.
4. **IA em lote** (`BatchCategoryClassifier`, já desenhado na fase 10 de IA): só para o que sobrou, agrupado
   por chave de estabelecimento, até 100 chaves por chamada, temperatura 0, esquema com `enum` da taxonomia
   v2 **e** campo `confidence ∈ {alta, media, baixa}`; prompt com a descrição de cada grupo e 3 exemplos
   por grupo; cadeia `Ai__Chains__Categorize` do desenho de IA; filtro de privacidade da seção 3.8 daquele
   desenho; disponível com a IA ligada para o grupo (hoje ligada por padrão, #78). Resultado `baixa` é
   aplicado mesmo assim, marcado para revisão.
5. **Aprendizado coletivo** (minha escolha; **dono**, seção 15.5): tabela agregada `merchant_key → categoria →
   contagem de grupos distintos`, sem valores, sem datas e sem id de grupo, alimentada pelas correções. Quando
   ≥ 3 grupos concordam, vira elo entre 3 e 4. Fica desligado por configuração até a decisão.
6. **Padrão**: `OUTROS`, Confiança Baixa.

Confiança e consequência: Alta → aplica em silêncio. Média → aplica e mostra um ponto discreto "sugerida" no
lançamento, um toque confirma (vira regra). Baixa ou `OUTROS` → aplica e entra em "A revisar". Em nenhum caso
o lançamento deixa de contar.

### 6.3 Aprendizado por correção

Tabela `couple_category_rules` (desenho de IA, seção 9) estendida com `Subcategory?` e `IsShared?`:
- Mudar categoria ou escopo de um lançamento oferece "Aplicar aos próximos de [estabelecimento]" (padrão
  ligado quando há `Merchant`; o Monarch pergunta, o Copilot aplica; aqui aplica com desfazer no Toast). Grava
  a regra; `PATCH /transactions/{id}` responde `learnedRule: true`.
- Opcional na mesma folha: "Também aos anteriores" recategoriza em lote os lançamentos do mesmo estabelecimento
  no grupo (até 500; acima disso, só os do ano corrente).
- Mais → Regras de categoria lista e apaga (tela da fase 10 de IA, mantida).

### 6.4 Importação direta e revisão opcional

**Open Finance.** Linha sincronizada vira `Transaction` **na mesma operação**, com `Kind`, categoria e escopo
da cascata; `BankTransactionReviewState` passa a `Confirmed` automaticamente, e os estados `Reconciled`,
`Discarded` e `Ignored` continuam existindo para as ações da pessoa (desfazer uma fusão, excluir, ocultar).
Casos que iam para a tela de revisão passam a ser tratados assim:
- **Duplicata provável** (lançamento manual ou de notificação com mesmo valor, ±3 dias, mesma chave de
  estabelecimento) → **fusão automática**: a linha do banco vira o lançamento novo (data e valor do banco), a
  categoria, o escopo e as notas da pessoa são copiados para ele, e o lançamento antigo **continua existindo**
  com `MergedIntoId` = novo e `ExcludeFromTotals = true` (nada é apagado; `Transaction` não tem estado, e
  `BankTransaction.ReviewState` fica `Reconciled` como hoje). O antigo não aparece em listas nem totais; o
  detalhe do novo mostra "Fundido com o lançamento manual de 12/10 · Eram diferentes". A fusão fica em
  "A revisar" por 7 dias; desfazer limpa os dois campos e marca o par como "não fundir de novo".
- **Pendente no banco** (`BankTransactionStatus.Pending`) → entra como lançamento com selo "pendente", conta
  nos totais (minha escolha: o Copilot faz assim); quando o banco efetiva, atualiza-se no lugar.
- **Transferência interna, pagamento de fatura, crédito de membro** → `Kind` correspondente, fora dos gastos,
  sem revisão.
- **Categoria de confiança baixa** → "A revisar".
A tela `openfinance/review.tsx` e as rotas `review/confirm` e `review/restore` ficam até a fase 7 e são
removidas do app depois; a API as mantém marcadas obsoletas por uma versão.

**O que já está na fila de revisão em produção** (minha escolha, coerente com "experiência primeiro"): na
fase 2, uma migration de dados com tabela de backup (modelo `NormalizeCategoriesAndCurrency`) converte **todas**
as linhas `Pending` existentes, de todo o histórico sincronizado, em lançamentos pela cascata; as `Ignored`
(transferências e faturas que a pré-classificação separou) viram lançamentos com o `Kind` correspondente, fora
dos gastos; `Discarded` continua fora. Totais de meses passados mudam, para cima, porque passam a contar o que
nunca foi confirmado; a folha de primeira abertura (seção 7.5) avisa isso. Não há corte "daqui para a frente".

**Extrato PDF.** A revisão vira "Conferir e importar": todas as linhas pré-selecionadas e já categorizadas; um
toque importa; a pessoa desmarca o que não quer. Linhas "Entrada" entram como `Receita`. Duplicatas prováveis
contra o que já existe aparecem desmarcadas com a explicação.

**Captura de notificação** (**dono**, seção 15.3): continua gravando direto, com `Kind = Despesa`, escopo
Pessoal, e passa pela mesma fusão de duplicatas quando a linha do banco chegar. Recomendação: manter como fonte
secundária; se o dono quiser aposentar, a fase 7 remove a entrada de Mais e o aceite, sem tocar no Kotlin.

### 6.5 Medição e recategorização em lote

Ninguém conecta no banco de produção (CLAUDE.md). O caminho é um **endpoint interno** `POST
/internal/jobs/recategorize` protegido pelo `INTERNAL_JOBS_SECRET` (o mesmo dos agendamentos do desenho de IA),
com `dryRun` e `coupleId?`, que roda a cascata sobre `transactions` + `BankTransaction` e devolve **só
agregados**: % do valor em OUTROS antes e depois, % por `Source`, distribuição de confiança, contagem por
categoria. Nenhuma linha individual sai. Um workflow manual `recategorizar.yml` chama o endpoint com o segredo
do GitHub e imprime o relatório no log; a esteira o dispara e lê o log, sem pedir nada ao dono.

- Fase 1 roda o lote em **todos os grupos** com Pluggy + dicionário (sem IA), gravando `PreviousCategory`.
- Fase 2 roda de novo só o que ficou em OUTROS, agora com IA em lote, dentro do orçamento de cota do grupo.
- Critério de saída da fase 2: OUTROS < 10% do valor do mês corrente no grupo do dono (o `coupleId` dele vem
  do relatório agregado por grupo, sem expor nada além do percentual).
- Testes: a cascata também roda em teste de integração com semente fixa sobre um conjunto sintético de 500
  linhas com a distribuição do Pluggy, para medir falso positivo antes de qualquer revisão (lição de 09/10).

## 7. Telas

Todas em português, com `accessibilityLabel` como hoje, estados vazio/erro/carregando dos componentes existentes,
e reinício de estado por visita. Mockups de baixa fidelidade; o implementador segue a skill `frontend-design` e
a `dataviz` para gráficos.

### 7.1 Início

```
 [Eu | Nós]        outubro ▾                          ⚙
 ────────────────────────────────────────────────────────
 Gasto até hoje                              R$ 3.420
 ████████████████░░░░░░░░░░░   de R$ 5.000 orçados
 Pode gastar R$ 1.580 até dia 31 · R$ 105 por dia
 ▲ 12% a mais que em setembro no mesmo dia
 ────────────────────────────────────────────────────────
 ⚠ A revisar (3)                                      ›
   2 com categoria incerta · 1 pendente no banco
 ────────────────────────────────────────────────────────
 Por categoria                          Ver orçamento ›
 ● Mercado              R$ 980   ███████░░░  de 1.200
 ● Restaurantes         R$ 640   ██████████  de 500 !
 ● Transporte           R$ 410   █████░░░░░  de 800
 ● Moradia              R$ 1.100 ██████████  de 1.100
 ● Outros               R$ 90
 ────────────────────────────────────────────────────────
 [Nós] Por pessoa                                      ›
 Lucas  R$ 2.100 compartilhado · total R$ 3.420 (liberou)
 Ana    R$ 1.320 compartilhado
 Conta conjunta Itaú  R$ 800
 ────────────────────────────────────────────────────────
 Contas                                   R$ 4.230 saldo
 Nubank ···1234 R$ 3.100 · Fatura Nubank R$ 1.890 vence 10/11
 ────────────────────────────────────────────────────────
 Previsão para o fim do mês        sobra R$ 1.200
 ────────────────────────────────────────────────────────
 Últimos lançamentos                        Ver todos ›
 hoje   iFood · Restaurantes                 R$ 54,90
 hoje   Uber · Transporte                    R$ 18,40
 ontem  Supermercado BH · Mercado           R$ 312,70
 ────────────────────────────────────────────────────────
 💬 Assistente                                        ›
```

Regras:
- O bloco do topo é o único obrigatório; os outros aparecem só quando há conteúdo (sem orçamento → "Definir
  orçamento" no lugar da barra; sem contas conectadas → cartão "Conectar banco" uma vez, dispensável).
- "Pode gastar" = (orçado − gasto) ÷ dias restantes; sem orçamento, mostra só "gasto até hoje" e a comparação
  com o mês anterior no mesmo dia (`GET /dashboard` ganha `previousPeriodSameDay`).
- "Por pessoa" só no Nós: cada membro com o que compartilhou e, se liberou (seção 4.2), o total pessoal;
  tocar abre o detalhe daquele membro até o nível liberado. "Contas" só com Open Finance (saldo e fatura vêm
  de `BankAccount`, já guardados [código]); no Nós mostra só contas compartilhadas e as que o membro liberou.
  "Previsão" reaproveita `GET /cashflow` (horizonte 30).
- "A revisar" é um inbox: lista com ações em linha (confirmar categoria, escolher outra, "eram diferentes",
  dispensar). Dispensar tudo é um toque. Nunca mostra contagem maior que 99.
- O espaço do insight da IA (fase 7 do desenho de IA) é uma linha sob o bloco do topo, só quando houver.
- Rolagem: o bloco do topo e "A revisar" cabem na primeira tela de um aparelho de 6".

### 7.2 Lançamentos

```
 [Eu | Nós]   🔍 Buscar                      ⋯ (importar extrato, mostrar transferências)
 [Todas as pessoas ▾] [Categoria ▾] [Conta ▾] [outubro ▾]
 ────────────────────────────────────────────────────────
 Hoje                                        R$ 73,30
 ● iFood                 Restaurantes · você   R$ 54,90
 ● Uber                  Transporte · você     R$ 18,40
 Ontem                                       R$ 312,70
 ● Supermercado BH       Mercado · Ana · 👥    R$ 312,70
 14 de outubro                               R$ 1.100,00
 ● Aluguel               Moradia · você · 👥  R$ 1.100,00
 ...                                              (+)
```

- Lista agrupada por dia com total do dia; linha = ícone colorido da categoria, estabelecimento (ou
  descrição), categoria · pessoa (no Nós) · marca de compartilhado, valor. Selo discreto para "pendente" e
  "sugerida". Transferências ocultas por padrão.
- Deslizar para a esquerda: trocar categoria (abre o seletor). Deslizar para a direita: alternar
  compartilhado/pessoal. Toque: folha de detalhe (bottom sheet) com valor, estabelecimento, data, conta e
  origem, seletor de categoria (busca, recentes, sugeridas, dois níveis), alternador Eu/Nós, "Aplicar aos
  próximos de [estabelecimento]", nota, excluir, "Não contar nos totais".
- Busca por texto em estabelecimento e descrição (API: `GET /transactions?q=`). Filtros: pessoa (novo filtro
  `userId`), categoria (grupo ou subcategoria), conta (`accountId`), tipo, período.
- Botão flutuante "+" abre o formulário manual: valor com teclado numérico grande, Despesa/Receita,
  estabelecimento, categoria sugerida pela cascata enquanto digita (debounce), Eu/Nós, data (hoje), conta
  opcional. Dois toques e pronto.

### 7.3 Orçamento

```
 [Eu | Nós]        outubro ▾
 ────────────────────────────────────────────────────────
 Renda R$ 8.000 · Orçado R$ 5.000 · Gasto R$ 3.420 · Livre R$ 3.000
 ────────────────────────────────────────────────────────
 Moradia            R$ 1.100 de 1.100   ██████████   0 restante
 Mercado            R$ 980 de 1.200     ████████░░   220 restante
 Restaurantes       R$ 640 de 500       ██████████   140 acima !
 Transporte         R$ 410 de 800       █████░░░░░   390 restante
 Assinaturas        R$ 120 de 150       ████████░░   30 restante
 ────────────────────────────────────────────────────────
 Sem limite                                 R$ 170 gastos
 Saúde R$ 80 · Compras R$ 60 · Outros R$ 30         Definir ›
 ────────────────────────────────────────────────────────
 Renda do mês                                 Editar ›
 Salário (banco) R$ 6.500 · Freela R$ 1.500 (cadastrado)
```

- Toque numa categoria: teclado numérico em linha para o limite; "Usar média dos últimos 3 meses" preenche com
  a média arredondada para cima em dezenas. Subcategorias expandem em leitura (gasto por subcategoria, sem
  limite próprio).
- Copiar do mês anterior ao abrir um mês sem plano (um toque; o Monarch faz "roll over budgets").
- "Renda do mês": **Eu** = minhas fontes (`IncomeSource` com meu `UserId`) + lançamentos `Receita` das minhas
  contas pessoais. **Nós** = renda total da casa: fontes de todos os membros, compartilhadas ou não, +
  `Receita` de todas as contas (é o "Renda Total do Casal" que a aba Rendas mostra hoje). Receita detectada
  pelo banco leva selo "banco"; se um valor igual já está cadastrado como fonte no mesmo mês, não soma duas
  vezes (mesma regra de fusão da seção 6.4, pelo nome da fonte ou valor exato). A aba Rendas deixa de existir;
  adicionar/editar fonte de renda fica aqui, no mesmo formulário de hoje, simplificado.
- Eu e Nós têm planos separados (`BudgetPlan.UserId`); o do Nós só existe se "Orçamento compartilhado" estiver
  ligado nas Configurações do círculo (senão a aba no Nós mostra só o gasto compartilhado por categoria, sem
  limites). "Livre" do Nós = renda total da casa − orçado do Nós;
  "Livre" do Eu = minha renda − orçado do Eu. Os dois "Livre" não se somam nem se descontam entre si (o Eu
  não sabe quanto a casa orçou; é informação, não contabilidade).
- Alertas de 80% e estouro passam a funcionar (hoje nunca disparam porque ninguém cria plano, issue #6).

### 7.4 Mais

Lista simples: Contas e bancos (conexões, contas com saldo e escopo Pessoal/Compartilhada, sincronizar,
desconectar; absorve `settings/openfinance/*`) · **Círculo** (nome, membros, convite, e as Configurações do
círculo da seção 4.2; absorve `settings/group`) · Metas · Relatórios · Regras de categoria · Assistente ·
Alertas · Captura de notificações · Privacidade · Conta (e-mail, senha, tema, sair). Um item por linha, sem
cartões, sem descrições longas. Issue #28 (Patrimônio) entra aqui quando for feita.

### 7.5 Primeira abertura depois da atualização

Uma folha única, uma vez: "O CoupleSync mudou: 4 abas, categorias novas, nada mais precisa ser confirmado.
Seus lançamentos foram recategorizados; confira em A revisar o que ficou incerto." com "Entendi". Sem tour.

## 8. Sistema visual

- **Tokens** (`mobile/src/theme/`): cores semânticas (fundo, superfície, texto em três níveis, borda, primário,
  positivo, negativo, atenção) em dois temas, claro e escuro, escolhidos pelo sistema (`useColorScheme`,
  `userInterfaceStyle: "automatic"`) com opção manual em Mais → Conta (**dono**, seção 15.4: hoje é só
  escuro). 16 cores de categoria com contraste AA sobre as duas superfícies, validadas pelo método da skill
  `dataviz`; cada grupo de categoria tem a sua, sempre a mesma em toda tela.
- **Tipografia**: Inter via `@expo-google-fonts/inter`, algarismos tabulares (`fontVariant: ['tabular-nums']`)
  em todo valor monetário; escala 12/14/16/20/28/36.
- **Componentes** (`mobile/src/components/ui/`): `Card`, `Row` (linha de lista com ícone/título/legenda/valor),
  `Amount` (formata BRL, cor por sinal, tabular), `CategoryIcon`, `CategoryPicker` (dois níveis, busca,
  recentes), `ProgressBar`, `SegmentedControl` (Eu | Nós), `BottomSheet`, `FilterChip`, `Button` (primário,
  secundário, destrutivo), `MonthPicker`, `Badge` (pendente, sugerida, banco). Tudo com `accessibilityRole` e
  rótulos. As telas novas usam só isso; `EmptyState`, `ErrorState`, `LoadingState`, `Toast` continuam.
- **Movimento**: só o já existente (`reanimated` nos ícones de aba) e a transição da folha. Sem confete.
- **Sem dependência nativa nova até a fase 7** [código: `react-native-gesture-handler` não está no app]:
  deslizar nas linhas usa `PanResponder` do React Native; a folha de detalhe usa `Modal` + `Animated`. O tema
  pelo sistema exige `userInterfaceStyle: "automatic"` em `app.json` (configuração nativa → APK): nas fases
  0–6 o tema é escolhido manualmente em Mais → Conta (padrão: escuro, como hoje); a fase 7 publica o APK e
  liga o automático. Se a fase 5 concluir que `PanResponder` não dá a sensação esperada, a troca por
  `gesture-handler` entra junto do APK da fase 7, não antes.
- **Gráficos**: `react-native-gifted-charts` continua em Relatórios; o Início e o Orçamento usam barras simples
  em `View`, sem biblioteca.

## 9. API (resumo das mudanças, todas aditivas até a fase 7)

| Rota | Mudança |
|---|---|
| `GET /categories` | itens ganham `parent`, `icon`, `color`, `kind`; novos itens da taxonomia v2 |
| `GET /transactions` | novos filtros `scope=me\|shared`, `userId`, `accountId`, `kind`, `q`, `includeTransfers`; itens ganham `kind`, `subcategory`, `isShared`, `confidence`, `pending`, `accountId`, `mergedFromId` |
| `POST /transactions`, `PATCH /transactions/{id}` | aceitam `kind`, `subcategory`, `isShared`, `applyToMerchant`, `applyToPast`, `excludeFromTotals`; resposta com `learnedRule` |
| `GET /transactions/suggest?merchant=&amount=` | nova: sugestão da cascata para o formulário manual |
| `GET /dashboard` | ganha `scope`, `budgetTotal`, `safeToSpend`, `previousPeriodSameDay`, `byMember` (no shared), `accounts` (saldo e fatura), `reviewCount`, `forecastEndOfMonth` |
| `GET /review` (novo, substitui `openfinance/review`) | itens de "A revisar" com tipo e ações; `POST /review/{id}/resolve` |
| `GET/PUT /budgets/...` | `scope`; `grossIncome` calculado; `PUT allocations` por grupo de categoria; `POST /budgets/{month}/copy-previous`, `GET /budgets/{month}/suggestions` |
| `PATCH /openfinance/accounts/{id}` | ganha `scope` |
| `GET/PUT /couples/me/sharing` (novo) | configurações do círculo do membro: `visibility` (shared\|totals\|all), `defaultSharedCategories[]`, `sharedBudgetEnabled`; `GET /couples/me` devolve a visibilidade de cada membro para quem pergunta |
| `POST /auth/register` | cria o círculo de uma pessoa junto com a conta; `couple-setup` deixa de ser obrigatório |
| `GET /ai/category-rules`, `DELETE ...` | da fase 10 de IA, com `subcategory` e `isShared` |
| `openfinance/review*`, `cashflow` | mantidas até a fase 7; depois `review*` sai do app e é marcada obsoleta |

Migrations: `Kind`, `IsShared`, `Subcategory`, `Confidence`, `PreviousCategory?`, `AccountId?`,
`MergedIntoId?`, `ExcludeFromTotals` em `transactions`; conversão das linhas `Pending`/`Ignored` existentes
em lançamentos (fase 2, com tabela de backup); `Scope` em `bank_accounts`; `UserId?` em `budget_plans`; `couple_category_rules` (+
`subcategory`, `is_shared`); `merchant_category_votes` (coletivo, só se aprovado). Todas aditivas, com
preenchimento de padrão em migration de dados separada (`Kind = Despesa`, `IsShared = true`).

## 10. O que sai, o que fica, o que é absorvido

| Item | Destino |
|---|---|
| Abas Fluxo, Rendas, Relatórios, Metas | Fluxo vira linha do Início; Rendas entra em Orçamento; Relatórios e Metas vão para Mais |
| `openfinance/review.tsx`, `ocr-review` como tela de validação | Substituídas por "A revisar" e "Conferir e importar" |
| `QuickIncomeModal` | removido (sem uso) [código] |
| Issue #47 (categorias aprendidas e lote) | absorvida pela fase 2 |
| Issue #6 (tela de orçamento) | absorvida pela fase 5 |
| Issue #48 (duplicadas entre fontes) | absorvida pela fase 2 (fusão automática) |
| Issue #15 (ajustes de usabilidade) | itens 3, 4, 5 e 7 ficam obsoletos com as telas novas; 1, 2 e 6 continuam |
| Issue #5 (grupos com nome) | pequena, entra na fase 3 (rótulo do "Nós") |
| Issue #7 (renda recorrente) | entra em Orçamento, fase 5 |
| Issue #28 (Patrimônio) | fica no backlog, com lugar previsto em Mais → Contas |
| Issues #39/#40 (recorrências, assistente com contexto) | ficam como estão; o Início tem lugar para "Recorrentes próximas" quando a #39 for redesenhada |
| Fluxos Maestro `05-metas-e-rendas`, `06-abas`, `04-transacao-manual` | reescritos na fase 6 |

## 11. Riscos

- **Recategorização em lote dos dados reais**: roda primeiro em cópia (script com `--dry-run` e relatório),
  com os dados do dono como teste de aceite; nunca apaga a categoria antiga (coluna `PreviousCategory` por
  uma versão).
- **Fusão automática errada**: desfazer em "A revisar" por 7 dias; limite conservador (valor exato, ±3 dias,
  mesma chave) medido com dados sintéticos de semente fixa antes da revisão (lição de 09/10).
- **Privacidade do pessoal**: a visibilidade por membro muda o que o parceiro vê; a migração preserva o que os
  casais de hoje veem (tudo compartilhado) e a tela de Privacidade explica os três níveis. Toda rota que
  devolve lançamento, conta ou saldo passa pelo mesmo filtro de visibilidade (um só ponto, testado por rota).
- **APK antigo durante o OTA**: fases 1–3 só adicionam campos; o 1.3.0 continua lendo. A fase 6 (navegação)
  é só JavaScript.
- **Cota de IA**: a cascata chama a IA só no resto; estimativa < 10 chamadas/dia por grupo; orçamento do
  desenho de IA vale.
- **Render gratuito** (512 MB): recategorização em lote em páginas de 500 com `SaveChanges` por página.
- **Testes travados**: paridade de categorias app × API e `PluggyCategoryMap` são reescritos na fase 1, não
  "ajustados".

## 12. Como saber que deu certo

- OUTROS < 10% do valor gasto do mês nos dados do dono (medição da seção 6.5).
- Zero passos obrigatórios entre sincronizar e ver o número (teste de integração: sync → `GET /dashboard`
  reflete o valor).
- 4 abas; fluxo Maestro novo toca as 4 e verifica Eu | Nós.
- Checkpoint de aparelho (issue #17) com o roteiro: "quanto gastei com restaurantes este mês?" respondido
  em < 10 s pelo dono sem ajuda; "isso foi meu ou nosso?" idem.
- Ele dizer que gosta. É o critério que faltou até aqui.

## 13. Fases (cada uma é uma issue e uma entrega pela esteira)

Ordem: dados primeiro, porque alivia as duas dores maiores (Outros e validação) em poucos dias e porque toda
tela nova depende dos campos novos. Interface na sequência.

| # | Fase | Entrega | Publicação |
|---|---|---|---|
| 0 | **Fundação visual** | tokens, temas claro/escuro (escolha manual até o APK da fase 7), fonte, 12 componentes de `ui/` com testes de lógica; nenhuma tela muda | OTA |
| 1 | **Taxonomia v2 e tipo** | `Kind`, `Subcategory`, categorias v2 com ícone e cor, `PluggyCategoryMap` completo, dicionário ~300, migration de dados, script de recategorização com relatório antes/depois, `GET /categories` novo; app lê os rótulos novos | API, depois OTA |
| 2 | **Cascata, aprendizado e importação direta** | `CategorizationPipeline`, regras do grupo, IA em lote com confiança, fusão automática de duplicadas, Open Finance grava direto, pendentes contam, "A revisar" na API; absorve #47 e #48 | API (migration), OTA |
| 3 | **Eu \| Nós e círculo** | `IsShared`, `Scope` por conta (padrão Pessoal), visibilidade por membro e filtros de API conforme 4.2, `BudgetPlan.UserId`, regras de escopo, círculo de uma pessoa no registro, tela Configurações do círculo e folha de entrada, nome do grupo (#5) | API (migration), OTA |
| 4 | **Início novo** | tela 7.1 completa com `GET /dashboard` estendido, "A revisar" no app, folha de primeira abertura | OTA |
| 5 | **Orçamento e Lançamentos** | telas 7.2 e 7.3, folha de detalhe, deslizar, busca, filtros, formulário manual novo com sugestão; Rendas absorvida; absorve #6 e #7 | OTA |
| 6 | **Navegação de 4 abas** | `_layout.tsx` novo, Mais, remoção de Fluxo/Rendas/Relatórios/Metas das abas, remoção de `openfinance/review` e `QuickIncomeModal`, "Conferir e importar", Maestro reescrito | OTA |
| 7 | **Polimento e medição** | acessibilidade, tema claro conferido tela a tela, desempenho da lista, relatório final da seção 12, rotas obsoletas marcadas, roteiro de checkpoint (#17) | OTA, tag e APK |

Issues (rótulos `backlog` + `redesenho`): fase 0 = #81, 1 = #82, 2 = #83, 3 = #84, 4 = #85, 5 = #86, 6 = #87,
7 = #88. Cada fase entra em `main` por PR com CI verde, como as anteriores. Fases 1 e 2 têm migration: avisar as sessões
paralelas antes do merge (regra em vigor). Nenhuma fase toca `docs/` nem `render.yaml`.

## 14. O que fica para o implementador

- Lista final do dicionário de ~300 estabelecimentos (fonte: os `Merchant` reais anonimizados por chave +
  conhecimento geral), com teste de cobertura.
- Prompt e exemplos do classificador em lote; validação de tamanho como na fase 10 de IA.
- Paleta exata das 16 cores (validador da skill `dataviz`) e os ícones Ionicons de cada grupo.
- Detalhe do algoritmo de "pode gastar por dia" nos últimos dias do mês (mínimo R$ 0, nunca negativo).
- Comportamento da lista com mais de 2.000 lançamentos no mês (paginação por dia, `FlatList` com seções).
- Textos de todas as telas, em português, sem jargão ("lançamento", não "transação", na interface; a API não muda nomes).

## 15. Decisões do dono (16/10/2026)

Respostas dele às sete perguntas do rascunho, na sessão, com as palavras dele quando houve:

1. **Privacidade e modelo**: "tratar como orçamento individual por pessoa, e a visão do casal ser mais simples
   e direta (tipo top-view e low-view), mas [...] se a outra parte do casal quiser ver mais detalhes sobre
   transações e extrato da outra pessoa, deve haver a possibilidade. Talvez uma tela de configurações do
   casal/círculo/família pra definir esse tipo de coisa." → seção 4.2 inteira.
2. **Escopo da conta**: "respondido no 1. Tratar como orçamento individual, mas quando a pessoa conectar num
   círculo/casal/família, ter tudo parametrizável e customizável." → padrão Pessoal; Configurações do círculo
   e folha de entrada (4.2).
3. **Captura de notificação**: (a) manter como fonte secundária, com fusão quando a linha do banco chegar.
4. **Tema**: (a) claro e escuro pelo sistema, com opção manual.
5. **Aprendizado coletivo**: ele não entendeu a pergunta; fica a recomendação conservadora, (b) pronto e
   desligado por configuração, até ele pedir para ligar. (Explicado a ele: usar correções de um casal para
   ajudar outro, só pelo nome da loja, sem valores nem pessoas.)
6. **Metas**: (a) manter em Mais, só retocada.
7. **Ordem**: (a) dados primeiro (fases 1–3), telas depois.

Minhas escolhas já tomadas, para ele conferir enquanto lê: pendentes do banco contam nos totais; fusão de
duplicadas automática com desfazer (o antigo fica guardado, oculto); "aplicar aos próximos" ligado por padrão;
captura de notificação nasce Pessoal; conta conjunta é da casa e não entra no Eu de ninguém; renda do Nós é a
renda total da casa; **toda a fila de revisão do banco que já existe em produção vira lançamento** na fase 2
(totais de meses passados sobem); medição e recategorização por endpoint interno + workflow, só agregados;
nenhuma dependência nativa nova antes do APK da fase 7; `GrossIncome` passa a calculado; subcategoria sem
limite próprio; nome na interface é "lançamento".
