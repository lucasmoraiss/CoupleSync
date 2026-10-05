# Sessão 4 de teste exploratório — metas, fluxo de caixa, relatórios e dashboard

- **Data e hora da execução:** 05/10/2026, das 19:13:42 às 19:15:42 UTC (16:13 às 16:15 no horário de Brasília)
- **Commit testado:** `98e3f64`
- **Alvo:** API CoupleSync em `http://localhost:5000/api/v1` (caixa-preta, bash + curl)
- **Total de casos:** 260 (numerados de 4.001 a 4.260), além dos passos de preparo
- **Evidências:** `sessao-4.sh` (script reexecutável, e-mails únicos com prefixo `s4-`) e `sessao-4.log` (saída completa)
- **Identificador da execução:** `20261005191342-7938`

## Cenário preparado pelo script

- Casal X: usuários A e B. Casal Y: usuário C. Casal Z: usuário E (sem nenhuma transação). Usuário D sem casal.
- Transações do casal X (8, total 2252,50):
  - Outubro/2026 (mês corrente): Alimentacao 100 (A), Transporte 50 (B), Alimentacao 25,50 (A), Poupanca 500 (A), Poupanca 300 (B). Soma 975,50.
  - Setembro/2026: Lazer 200 em 15/09 (A).
  - Agosto/2026: Moradia 1000 em 10/08 (B).
  - Saude 77 em `2026-09-01T02:30:00Z`, que é 31/08 às 23:30 no horário de Brasília (A).
- Transação do casal Y: Viagem 999 (C).
- Rendas do casal X em `/api/v1/incomes`: 5000 (pessoal de A) + 3000 (compartilhada de B) em 2026-10, total do casal 8000; 4000 em 2026-09.

## O que testei e funcionou

**Metas — criar, listar, detalhar, editar**
- 4.001 criar meta válida (201). 4.002 listar. 4.003 detalhar. 4.004 o parceiro B enxerga a meta criada por A.
- 4.005 editar só o título. 4.006 editar só o valor-alvo. 4.008 editar só o prazo. 4.009 editar só a descrição. 4.010 editar título, alvo, valor atual e prazo juntos (pelo parceiro B). 4.012 as edições persistem.
- 4.011 edição com corpo `{}` recusada com 400.
- 4.013 prazo no passado recusado na criação (400, mensagem em português).
- 4.016 criação com prazo hoje 23:59:59Z aceita. 4.066 edição para prazo hoje 23:59:59Z aceita.
- 4.017 e 4.018 valor-alvo zero e negativo recusados na criação. 4.055 e 4.056 recusados também na edição.
- 4.020 valor-alvo 9999999999999999,99 aceito e gravado. 4.031 valor-alvo 0,01 aceito.
- 4.022, 4.023 e 4.062 valor-alvo com 29 e 40 dígitos recusado com 400 (mas veja o formato da mensagem na tabela de problemas).
- 4.032 e 4.033 título vazio e só com espaços recusados na criação. 4.057 e 4.058 recusados na edição.
- 4.034 e 4.059 título com 300 caracteres recusado (limite de 128) na criação e na edição.
- 4.035 sem `title` recusado. 4.038, 4.039 e 4.040 prazo em texto, 31/02 e valor em texto recusados com 400. 4.046 corpo `{}` recusado listando os três campos.
- 4.041 moeda com 4 letras recusada. 4.042 moeda USD aceita. 4.043 e 4.068 descrição com 3000 caracteres recusada (limite de 512) na criação e na edição.
- 4.044 título com `<script>` gravado e devolvido como texto puro em JSON (a API não interpreta; o cuidado fica com quem exibe).
- 4.045 JSON malformado recusado com 400. 4.047 `Content-Type: text/plain` recusado com 415.
- 4.048 meta com título repetido é permitida (201).
- 4.069 as tentativas inválidas de edição não alteraram a meta. 4.070 e 4.086 restauração.
- 4.072 e 4.073 meta com prazo hoje (23:59:59Z) pode ser editada (título, e valor atual quando o título vai junto). 4.077 e 4.078 o mesmo para meta cujo prazo de hoje já venceu.
- 4.082 e 4.084 valor atual 15000 em meta de alvo 10000 é aceito e gravado (sem trava). 4.085 no resumo o percentual fica limitado a 100 e `isAchieved` vira `true`.

**Metas — arquivar e excluir**
- 4.087 e 4.088 arquivar (status `Archived`). 4.089 detalhar meta arquivada. 4.090 a lista padrão não mostra a arquivada (11 itens). 4.091 `includeArchived=true` mostra (12 itens). 4.092 `includeArchived=banana` recusado com 400.
- 4.093 editar meta arquivada devolve 409 `GOAL_ARCHIVED`. 4.094 arquivar de novo é idempotente (200). 4.095 progresso de meta arquivada responde.
- 4.097 e 4.098 excluir (204). 4.099, 4.100 e 4.101 meta excluída devolve 404 para detalhar, excluir de novo e editar. 4.102 a excluída não aparece nem com `includeArchived=true`.
- 4.103, 4.104, 4.106 a 4.110 id inexistente devolve 404 `GOAL_NOT_FOUND` em detalhar, editar, excluir, arquivar e progresso.

**Progresso — vínculo de transação**
- 4.112 progresso inicial zerado. 4.113 e 4.114 vincular a transação de 500: `/goals/{id}/progress` mostra 500 e 25%.
- 4.118 e 4.119 vincular duas vezes a mesma transação não duplica (segue 500).
- 4.120 e 4.121 o parceiro B vincula 300: progresso 800 e 40%.
- 4.126, 4.127 e 4.128 desvincular com `{"goalId":null}` volta o progresso para 0. 4.132 desvincular o que já está sem meta devolve 204.
- 4.134 `goalId` que não é GUID recusado com 400.
- 4.138 e 4.139 vincular a meta inexistente e a meta excluída devolve 404.
- 4.141 A não consegue vincular transação do casal X a meta do casal Y (404). 4.142 C não consegue vincular transação dele a meta do casal X (404). 4.143 C não consegue mexer em transação do casal X (404). 4.144 transação inexistente (404). 4.145 e 4.146 os progressos das duas metas seguem em 0.
- 4.147 a 4.149 valor vinculado (500) maior que o alvo (400): percentual limitado a 100 e `isAchieved: true`.
- 4.151 a 4.155 excluir uma transação vinculada retira o valor dela do progresso (540 volta para 500).
- 4.156 a 4.159 arquivar e excluir meta com transação vinculada funcionam; a transação continua existindo (8 transações).

**Fluxo de caixa**
- 4.160 `horizon=30`: 6 transações, total 1175,50 (confere: 100 + 50 + 25,50 + 500 + 300 + 200). 4.161 `horizon=90`: 8 transações, total 2252,50 (confere com tudo o que foi criado). As somas por categoria conferem.
- 4.162 a 4.166 sem parâmetro, 0, -30, 100000 e 60 recusados com 400 `INVALID_HORIZON`. 4.167 a 4.170 texto, 30.5, estouro de inteiro e vazio recusados com 400.
- 4.171 o parceiro B vê o mesmo resultado. 4.172 e 4.173 casal sem transações devolve tudo zerado, sem erro. 4.174 o casal Y vê só os 999 dele.

**Relatórios**
- 4.175 `spending-by-category` padrão: Moradia 1000, Poupanca 800, Lazer 200, Alimentacao 125,50, Saude 77, Transporte 50 — confere com as transações. 4.176 `months=1` traz só outubro (975,50). 4.178 `months=3` e 4.182 `months=60` conferem. 4.191 o parceiro B vê o mesmo.
- 4.179, 4.180, 4.181 e 4.183 `months` 0, -1, 1000 e 61 recusados com 400. 4.184 e 4.185 texto e 1.5 recusados com 400.
- 4.190 casal sem transações devolve lista vazia.
- 4.192 a 4.195 e 4.199 `monthly-trends`: os valores de despesa conferem em UTC (ago 1000, set 277, out 975,50) e os meses sem movimento vêm zerados. 4.196, 4.197, 4.198 e 4.200 `months` 0, -1, 1000 e texto recusados com 400. 4.204 casal sem transações devolve meses zerados.
- 4.205 e 4.206 rotas inexistentes devolvem 404.

**Dashboard**
- 4.207 sem parâmetros: mês corrente em UTC, total 975,50, 5 transações, A 625,50 e B 350 — confere. 4.208 o parceiro B vê o mesmo.
- 4.209 só `startDate` (01/08): vai até o fim do mês corrente, total 2252,50 e 8 transações — confere.
- 4.211 só `endDate` dentro do mês corrente responde. 4.220 só `endDate` = hoje e 4.221 só `startDate` = hoje respondem com 975,50.
- 4.213 período cobrindo tudo: 2252,50, A 902,50 e B 1350 — confere. 4.214 setembro em UTC: 277. 4.215 agosto em UTC: 1000.
- 4.216 e 4.217 agosto no horário de Brasília (limites com `-03:00` ou em `Z` equivalente): 1077, isto é, o offset na query é respeitado.
- 4.222 e 4.223 `endDate` só com a data inclui o dia inteiro (as transações de hoje entram).
- 4.224 datas invertidas recusadas com 400. 4.225 período sem dados devolve zeros. 4.226, 4.227 e 4.228 data inválida, 31/02 e formato dd/mm/aaaa recusados com 400. 4.229 e 4.230 períodos enormes respondem sem erro. 4.231 casal sem transações devolve zeros.
- 4.232 a lista de transações confere com as somas usadas acima.

**Isolamento entre casais**
- 4.233 a 4.237 C não consegue ler, editar, ver o progresso, arquivar nem excluir a meta do casal X (404 em todos). 4.238 a meta segue intacta.
- 4.239 e 4.240 a lista e o resumo de C só têm a meta do casal Y.
- 4.241 a 4.244 relatórios e dashboard de C só mostram os 999 dele. 4.245, 4.246 e 4.247 passar `coupleId` do casal X na query não muda nada.

**Autenticação**
- 4.248 a 4.252 sem token ou com token inválido: 401 nos quatro módulos.
- 4.253 a 4.257 usuário sem casal: 403 `COUPLE_REQUIRED` nos quatro módulos.
- 4.096, 4.258, 4.259 e 4.260 método não suportado: 405.

## O que testei e não funcionou — o que deve ser corrigido

| Caso | O que fiz | O que esperava | O que aconteceu (status + trecho) | Gravidade |
|---|---|---|---|---|
| 4.007, 4.049, 4.050, 4.063, 4.071, 4.076, 4.081 | `PATCH /goals/{id}` mandando só `{"currentAmount":1500}` (sem título nem prazo) | 200 com o valor atual atualizado; é a edição mais comum de uma meta | **400** `{"errors":{"Request":["At least one field must be provided for update."]}}`. A validação não conta `currentAmount` como campo. O mesmo valor é aceito se o título for junto (4.082, 4.072) | Alta |
| 4.051, 4.053 | `PATCH /goals/{id}` com `{"title":"Viagem Japao","currentAmount":-50}` e com `{"currentAmount":-50,"deadline":...}` | 400 dizendo que o valor atual não pode ser negativo | **500** `{"code":"INTERNAL_SERVER_ERROR","message":"An unexpected error occurred."}`. Não há validação de `currentAmount` na edição (a meta não foi alterada, 4.052) | Alta |
| 4.114 a 4.117, 4.123 a 4.125, 4.083 a 4.085, 4.149 e 4.150 | Comparei o progresso da mesma meta em `GET /goals/{id}/progress`, `GET /goals`, `GET /goals/{id}` e `GET /goals/progress-summary` | Os endpoints mostrarem o mesmo progresso | Números diferentes. Com 500 vinculados: progress `"contributedAmount":500.00,"progressPercent":25.00`; lista e detalhe `"currentAmount":0.00`; resumo `"progressPercent":0`. Com 800 vinculados e valor manual 100: progress 800 e 40%; resumo 100 e 5%. Com valor manual 15000 em alvo 10000: progress `"progressPercent":0,"isAchieved":false`; resumo `"progressPercent":100,"isAchieved":true`. Com 500 vinculados e alvo 400: progress `"isAchieved":true`; resumo `"isAchieved":false`. O `/progress` só soma transações vinculadas; lista e resumo só usam o `currentAmount` manual | Alta |
| 4.193, 4.194, 4.195 | `GET /reports/monthly-trends` com renda cadastrada em `/incomes` (8000 em 2026-10 e 4000 em 2026-09) | `income` 8000 em outubro e 4000 em setembro; `net` = renda − despesa | **200** com `{"month":"2026-09","income":0,"expense":277.00,"net":-277.00},{"month":"2026-10","income":0,"expense":975.50,"net":-975.50}`. A renda vem sempre zero e o saldo sempre negativo | Alta |
| 4.019, 4.021, 4.064, 4.065 | Criar meta com alvo 1e16 e 1e18; editar com `targetAmount` 1e18; editar com `currentAmount` 1e18 (+ título) | 400 informando o valor máximo | **500** `INTERNAL_SERVER_ERROR`. O limite prático é 9999999999999999,99 (4.020 aceito); acima disso estoura sem validação | Média |
| 4.026 a 4.029 | Criar meta com `targetAmount: 0.001` | 400 (menos de um centavo) ou gravar sem arredondar para zero | **201**; a meta é gravada com `"targetAmount":0.00` (4.027). Depois `GET /goals/{id}/progress` devolve **500** (4.028) e o resumo mostra `"progressPercent":0,"isAchieved":true` (4.029) | Média |
| 4.024, 4.025, 4.054, 4.066 | Criar meta com alvo 100.999; editar com valor atual 10.555 | 400 por ter mais de duas casas, ou a resposta já mostrar o valor gravado | **201** com `"targetAmount":100.999`, mas o valor gravado é `101.00` (4.025). Na edição a resposta mostra `"currentAmount":10.555` e o gravado é `10.56`. Arredondamento silencioso e resposta diferente do que ficou salvo | Baixa |
| 4.210, 4.212, 4.219 | `GET /dashboard` só com `startDate` em mês futuro; só com `endDate` anterior ao mês corrente (2020-01-31 e 30/09/2026) | 200 com o período pedido, ou 400 explicando que falta a outra data | **500** `INTERNAL_SERVER_ERROR`. A data que falta é preenchida com o início ou o fim do mês corrente e o período fica invertido sem validação. Pedir "tudo até o fim do mês passado" quebra | Média |
| 4.194, 4.177, 4.214 a 4.216 | Transação de 31/08 às 23:30 de Brasília (`2026-09-01T02:30:00Z`) | Cair em agosto para um casal brasileiro, ou existir um parâmetro de fuso horário | Cai em **setembro**: `monthly-trends` mostra ago 1000 e set 277; `spending-by-category?months=2` inclui Saude 77; dashboard de setembro em UTC dá 277. Só o dashboard permite contornar passando as datas com `-03:00` (4.217); os relatórios e o dashboard sem parâmetros fecham o mês em UTC | Média |
| 4.014, 4.015, 4.061, 4.067, 4.079 | Prazo "hoje" na criação e na edição | A mesma regra nas duas operações | Criar aceita hoje 00:00Z e hoje só com a data (**201**; mensagem da regra: "O prazo deve ser hoje ou uma data futura."). Editar recusa os mesmos valores (**400** `"Deadline must be a future date."`). Também não dá para reenviar o prazo que a meta já tem depois que ele vence (4.079), o que quebra um formulário que manda todos os campos | Média |
| 4.135, 4.136 | `PATCH /transactions/{id}/goal` apontando para meta arquivada | 409 ou 400, como acontece ao editar meta arquivada (4.093) | **204**; o progresso da meta arquivada passa a `"contributedAmount":500.00,"progressPercent":100,"isAchieved":true` | Média |
| 4.160, 4.161, 4.174 | `GET /cashflow?horizon=30` e `90`: comparei `projectedSpend` com `totalHistoricalSpend` | Uma projeção que acrescente informação (por exemplo, saldo previsto considerando renda, ou gasto previsto até o fim do período) | `projectedSpend` é sempre igual ao total já gasto: `"totalHistoricalSpend":1175.50 ... "projectedSpend":1175.5000000000000000000000000`; no de 90 dias, 2252,50 nos dois; no casal Y, 999 nos dois. É a média diária da janela multiplicada pelo mesmo número de dias. Não considera renda nem saldo | Média |
| 4.160, 4.161, 4.174 | Formato dos números e do texto do fluxo de caixa | Valores monetários com duas casas; texto no singular quando há uma transação | `"averageDailySpend":39.183333333333333333333333333`, `"projectedSpend":1175.5000000000000000000000000`; `"assumptions":"Baseado em 1 transações nos últimos 30 dias"` | Baixa |
| 4.186 a 4.189, 4.201 a 4.203 | `spending-by-category` e `monthly-trends` com `startDate`/`endDate` válidos, invertidos, inválidos e de período vazio | Filtrar pelo período, ou recusar o parâmetro que não existe | **200** com resposta idêntica à do caso sem parâmetros (4.175 e 4.192). As datas são ignoradas em silêncio, inclusive `startDate=banana` | Baixa |
| 4.211, 4.213, 4.218 | `GET /dashboard` com `endDate` à meia-noite, por exemplo `startDate=2026-08-01T00:00:00Z&endDate=2026-09-01T00:00:00Z` (fim exclusivo) | Período até o instante informado: só os 1000 de agosto | **200** com `"periodEnd":"2026-09-01T23:59:59.999Z"` e `"totalExpenses":1077.00`. Hora 00:00 é tratada como "só a data" e o fim é esticado para o dia inteiro; entrou a transação de 01/09 às 02:30Z | Baixa |
| 4.022, 4.023, 4.038, 4.039, 4.040, 4.045, 4.062, 4.134 | Corpo com tipo errado, número grande demais ou JSON malformado | Mensagem em português, sem detalhe interno | **400** com `"The JSON value could not be converted to CoupleSync.Api.Contracts.Goals.CreateGoalRequest. Path: $.targetAmount \| LineNumber: 0 \| BytePositionInLine: 71."` e ainda `"request":["The request field is required."]`. Vaza nome de classe e namespace internos | Baixa |
| 4.036, 4.037 | Criar meta sem `deadline`; criar meta sem `targetAmount` | "O prazo é obrigatório" / "O valor-alvo é obrigatório" | **400** `"Deadline":["O prazo deve ser hoje ou uma data futura."]` e `"TargetAmount":["'Target Amount' must be greater than '0'."]`. Campo ausente é tratado como valor padrão e a mensagem engana | Baixa |
| 4.017, 4.032, 4.034, 4.041, 4.043, 4.060, 4.093, 4.099, 4.162, 4.179, 4.224, 4.253 e demais erros | Registrei formato e idioma dos corpos de erro | Um formato único e mensagens em português, já que o aplicativo é em português | Cinco formatos convivem: (1) ProblemDetails `{"type","title","status","errors","traceId"}` nas validações; (2) `{"code","message","traceId"}` em 403, 404, 409 e 500; (3) `{"code":"INVALID_HORIZON","message":"Horizon must be 30 or 90."}` sem `traceId` no fluxo de caixa; (4) texto puro `months must be between 1 and 60.` e `startDate must not be after endDate` em relatórios e dashboard; (5) corpo vazio em 401, 405 e 404 de id que não é GUID (4.105). Quase tudo em inglês (`'Title' must not be empty.`, `Goal not found.`, `Cannot update an archived goal.`, `You must be paired with a partner to access this resource.`); a única mensagem em português é a do prazo na criação | Baixa |
| 4.175, 4.176 | Somei os percentuais de `spending-by-category` | Soma 100,00 | 44,40 + 35,52 + 8,88 + 5,57 + 3,42 + 2,22 = **100,01**; com `months=1`, 82,01 + 12,87 + 5,13 = **100,01** | Baixa |
| 4.176, 4.178 | Comparei a cor de uma mesma categoria em períodos diferentes | A mesma categoria com a mesma cor | Poupanca vem `#6366F1` com `months=1` e `#F59E0B` com `months=3`; a cor é dada pela posição no ranking, não pela categoria | Baixa |
| 4.074, 4.080 | `GET /goals/{id}/progress` de meta com prazo hoje e de meta com prazo vencido há segundos | Dias restantes como número inteiro; meta vencida sinalizada | `"daysRemaining":0.1983201651111111` e `"daysRemaining":-7.688963425925926E-05` (fração em notação científica); a meta vencida segue com `"status":"Active"` e nada indica o vencimento | Baixa |
| 4.133 | `PATCH /transactions/{id}/goal` com corpo `{}` | 400 por faltar `goalId` | **204**: corpo vazio é entendido como desvincular | Baixa |

## Funcionalidade não testada (faltou ou não foi implementada)

- **Relatórios por período de datas:** `spending-by-category` e `monthly-trends` só aceitam `months` (1 a 60). Não existe filtro `startDate`/`endDate`; os casos de "período vazio", "datas invertidas" e "data inválida" só puderam mostrar que os parâmetros são ignorados (4.186 a 4.189, 4.201 a 4.203).
- **Renda nos relatórios:** não foi possível conferir `income` e `net` com valores reais porque a renda vem sempre zero (4.193 a 4.195). `spending-by-category` não tem campo de renda.
- **Fuso horário nos relatórios e no fluxo de caixa:** não há parâmetro de fuso; não deu para testar o fechamento de mês no horário de Brasília nesses endpoints.
- **Fluxo de caixa:** só existem os horizontes 30 e 90. Não há projeção de saldo, de renda nem de despesas recorrentes; não há filtro por categoria ou por parceiro. Não testei transação com data no futuro nem transação exatamente no limite da janela de 30 dias.
- **Dashboard:** não traz renda, orçamento, metas nem comparação com o mês anterior; não tem filtro por categoria ou por parceiro. Não testei transação no último segundo do mês (o fim padrão é `23:59:59Z`, sem os milissegundos).
- **Metas:** não existe desarquivar (reativar) meta, nem conclusão automática ou manual (os únicos status vistos foram `Active` e `Archived`); não há paginação nem ordenação na lista; não há endpoint para listar as transações vinculadas a uma meta. Não testei limpar a descrição (mandar `null` ou vazio), edição simultânea pelos dois parceiros, nem o que acontece ao vincular a uma segunda meta uma transação que já está vinculada a outra.
- **Moeda:** metas aceitam moeda diferente de BRL (4.042), mas não testei o efeito de misturar moedas no progresso e nos relatórios.
- **Valores de transação:** não testei transações negativas (estorno) nem de receita nos relatórios, porque o cadastro manual só tem valor e categoria e esse módulo é de outra sessão.
- **Token expirado e renovação de token** nos quatro módulos: fora do escopo desta sessão.
- **Carga e concorrência:** não testadas (outras quatro sessões rodavam ao mesmo tempo contra a mesma API; não houve interferência visível nos números desta sessão).
