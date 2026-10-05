# Sessão de teste exploratório nº 2 — Transações, ingestão de notificações e dashboard

- **Data e hora da execução:** 05/10/2026, 16:09:09 a 16:11:26 (UTC-03:00)
- **Commit testado:** `98e3f64`
- **Alvo:** `http://localhost:5000` (API CoupleSync em execução local), teste caixa-preta com `curl`
- **Escopo:** `/api/v1/transactions/*`, `POST /api/v1/integrations/events`, `GET /api/v1/integrations/status`, `GET /api/v1/dashboard`
- **Total de casos:** 156 (2.01 a 2.156); 12 respostas HTTP 500 registradas no log
- **Evidências:** `sessao-2.sh` (script reexecutável) e `sessao-2.log` (saída completa). Todos os números de caso abaixo referem-se ao cabeçalho `### CASO 2.NN` do log.
- **Cenário:** casal X = usuários A (Ana) e B (Bruno); casal Y = usuário C (Carla); usuário D sem casal. E-mails `s2-<letra>-<carimbo>@teste.local`, criados pelo próprio script.

Rotas existentes no escopo (descobertas nos controllers): `GET /transactions` (parâmetros `page`, `pageSize`, `category`, `startDate`, `endDate`), `POST /transactions`, `PATCH /transactions/{id}/category`, `PATCH /transactions/{id}/goal`, `DELETE /transactions/{id}`, `POST /integrations/events`, `GET /integrations/status`, `GET /dashboard` (parâmetros `startDate`, `endDate`).

---

## 1. O que testei e funcionou

### Caminho feliz
- **2.01 / 2.02** — Dashboard e lista de um casal novo vêm zerados (`totalExpenses:0`, `totalCount:0`).
- **2.03** — Criar transação manual válida → 201, com `bank:"MANUAL"`, `source:"Manual"`, `authorName` do autor.
- **2.04** — A transação aparece na lista de A.
- **2.05** — B (mesmo casal) vê a transação de A.
- **2.06** — Dashboard após criar 100,50: `totalExpenses:100.50`, `transactionCount:1`, categoria e parceiro corretos.
- **2.07 / 2.08** — `PATCH /{id}/category` altera a categoria (200) e o dashboard passa a mostrar só a nova categoria.
- **2.12** — B cria 50,25; dashboard soma 150,75 e separa `partnerBreakdown` por usuário (100,50 e 50,25).
- **2.13 / 2.15** — Filtro `category` exato devolve só a transação esperada; categoria inexistente devolve lista vazia.
- **2.16 / 2.19** — Filtro por `startDate`/`endDate` na lista e no dashboard devolve só o que está no período.
- **2.23 / 2.24** — `PATCH /{id}/goal` com `goalId:null` → 204; com meta inexistente → 404 `GOAL_NOT_FOUND`.
- **2.28** — Excluir transação já excluída → 404 `TRANSACTION_NOT_FOUND`.
- **2.29** — Após as exclusões, lista e dashboard voltam a zero.
- **2.72** — Data sem fuso (`2026-10-05T10:00:00`) na transação manual é aceita (201) e gravada como `10:00:00Z` (ver ressalva na tabela de problemas).
- **2.76 / 2.95** — Sem `eventTimestampUtc` usa o instante atual; corpo mínimo (`amount` + `category`) é aceito, moeda padrão `BRL`.
- **2.91** — Moeda `brl` em minúsculas é normalizada para `BRL`.
- **2.102** — Campos forjados no corpo (`userId`, `coupleId`, `source`, `bank`, `id`) são ignorados: a transação fica com o usuário autenticado, `bank:"MANUAL"`, `source:"Manual"`.
- **2.101** — Categoria com `'; DROP TABLE ...` é tratada como texto (gravada e filtrada literalmente; nada quebrou).

### Validações que responderam corretamente (4xx)
- **2.59 / 2.60** — Valor zero e negativo → 400 `INVALID_INPUT`.
- **2.65 / 2.67 / 2.68** — `amount` texto, com vírgula ou `null` → 400.
- **2.74 / 2.75** — Data em formato `05/10/2026` e data impossível (30/02) → 400.
- **2.83 / 2.84 / 2.87 / 2.88** — Categoria vazia, só espaços, ausente ou `null` → 400.
- **2.93 / 2.94** — `amount` ausente e corpo `{}` → 400.
- **2.96 / 2.97 / 2.98 / 2.99** — JSON malformado, corpo vazio, array → 400; `text/plain` → 415.
- **2.103 a 2.107** — `PATCH category` com categoria vazia, 65 caracteres, ausente, numérica ou JSON malformado → 400.
- **2.108 / 2.109 / 2.110 / 2.116** — Id inexistente (e GUID zerado) em `PATCH category`, `PATCH goal` e `DELETE` → 404 `TRANSACTION_NOT_FOUND`.
- **2.112 / 2.113 / 2.114** — Id que não é GUID → 404 (sem 500; ver ressalva de corpo vazio).
- **2.18 / 2.21 / 2.54 / 2.56** — `startDate` inválida, `page=abc`, `page=99999999999` → 400.
- **2.20** — Dashboard com `startDate > endDate` → 400.

### Isolamento entre casais
- **2.31** — C lista e recebe `totalCount:0`; nenhuma ocorrência da transação de X.
- **2.33 / 2.35 / 2.36** — C tenta recategorizar, vincular a meta e excluir a transação de X → 404 `TRANSACTION_NOT_FOUND` nos três.
- **2.37** — A confere: transação intacta (77,70, categoria `Casa`).
- **2.38 / 2.39** — Dashboard e status de integração de C não refletem nada de X.
- **2.128** — O mesmo evento enviado por C é `Accepted` para o casal Y (deduplicação é por casal) e cria 1 transação em Y sem afetar X.
- **2.40** — Usuário sem casal → 403 `COUPLE_REQUIRED` em listar e criar.
- **2.41 / 2.42** — Sem token ou token inválido → 401 em todas as rotas do escopo.

### Paginação (casal Y, 25 transações)
- **2.43** — 25 criações → 25 × 201.
- **2.44** — Sem parâmetros: `totalCount:25`, `page:1`, `pageSize:20`, 20 itens.
- **2.45 / 2.46 / 2.47** — `pageSize=10`: páginas 1, 3 e 4 devolvem 10, 5 e 0 itens, sempre `totalCount:25`.
- **2.48 / 2.49** — `pageSize=100` devolve 25; `pageSize=101` é limitado a 100.
- **2.50 a 2.53** — `page=0`, `page=-1`, `pageSize=0`, `pageSize=-5` caem nos padrões (página 1, tamanho 20) sem erro.
- **2.57** — Filtro + paginação: `category=Paginacao&page=2&pageSize=20` → 5 itens, `totalCount:25`.
- **2.58** — Dashboard de C: `totalExpenses:325.00`, `transactionCount:25` (soma de 1 a 25 confere).
- Ordenação observada: `eventTimestampUtc` decrescente, estável entre páginas.

### Ingestão de notificações
- **2.121 / 2.122** — Evento NUBANK 45,90 em "Supermercado Extra S2" → 201 `Accepted`; 1 transação criada com `bank:"NUBANK"`, `source:"Notification"`, categoria **`Alimentação`**.
- **2.124** — Mesmo evento 2× em sequência → `Accepted` e depois `Duplicate`; 1 transação.
- **2.125** — Mesmo evento 2× em paralelo → um `Accepted`, um `Duplicate`; 1 transação.
- **2.126** — Mesmo evento 6× em paralelo → 1 `Accepted`, 5 `Duplicate`; 1 transação.
- **2.127** — B reenvia o evento já enviado por A → `Duplicate`; continua 1 transação.
- **2.129** — Contador `totalDuplicate:8` confere com as duplicidades enviadas (1 + 1 + 5 + 1).
- **2.130** — Evento só com texto "Compra R$ 19,99 LOJA ABC" → `Accepted`; categoria **`OUTROS`** (descrição e estabelecimento ficam `null`: o texto bruto não é interpretado).
- **2.131** — Mesmo texto com `merchant:"LOJA ABC"` → categoria **`OUTROS`**.
- **2.132** — Categorização automática: UBER → `Transporte`; IFOOD → `Alimentação`; DROGARIA → `Saúde`; POSTO SHELL → `Transporte`; NETFLIX → `Lazer`.
- **2.135 / 2.136** — Valor negativo e zero → 400 "Amount must be greater than zero.".
- **2.138 / 2.139** — Valor `99999999999999999999` → 400 "Amount exceeds maximum allowed value."; valor texto → 400.
- **2.140 / 2.142 / 2.143** — Banco desconhecido, banco vazio e moeda `XYZ` → 400 com lista dos aceitos.
- **2.145 / 2.146** — `eventTimestamp` ausente → 400; futuro (2099) → 400 "cannot be in the future".
- **2.148 a 2.151** — JSON malformado, texto bruto de 20.000, `merchant` e `description` de 5.000 caracteres → 400.
- **2.152** — ITAU, BRADESCO, INTER e C6 → `Accepted`.

### Coerência de totais
- **2.118 / 2.155** — Recalculei a soma dos valores listados (em centavos) e ela bate com `totalExpenses` do dashboard, tanto no mês corrente (19 e 37 transações) quanto no período amplo (21 e 39). Casal Y no fim: 337,34 = 325,00 + 12,34 da notificação. O cálculo é aritmeticamente coerente (as ressalvas de moeda e arredondamento estão na tabela abaixo).

---

## 2. O que testei e não funcionou — o que deve ser corrigido

| Caso | O que fiz | O que esperava | O que aconteceu (status + trecho) | Gravidade |
|---|---|---|---|---|
| 2.73 | `POST /transactions` com `eventTimestampUtc":"2026-10-03T10:00:00-03:00"` (fuso de Brasília) | 201, gravando 13:00Z | **500** `{"code":"INTERNAL_SERVER_ERROR","message":"An unexpected error occurred."}` | Alta |
| 2.133 | `POST /integrations/events` com `eventTimestamp":"2026-10-05T10:00:00"` (sem `Z`) | 201 (como na transação manual) ou 400 explicando o formato | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.134 | `POST /integrations/events` com `eventTimestamp":"2026-10-04T10:00:00-03:00"` | 201 | **500** `INTERNAL_SERVER_ERROR`. Somado a 2.133: a ingestão só aceitou datas terminadas em `Z` | Alta |
| 2.144 | `POST /integrations/events` sem o campo `currency` | 400 "Currency is required" (ou padrão BRL) | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.147 | `POST /integrations/events` com corpo `{}` | 400 listando os campos obrigatórios | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.79 | `POST /transactions` com descrição de 513 caracteres | 400 "máximo 512" | **500** `INTERNAL_SERVER_ERROR` (512 caracteres foi aceito no 2.80) | Alta |
| 2.81 | `POST /transactions` com descrição de 5.000 caracteres | 400 | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.82 | `POST /transactions` com `merchant` de 5.000 caracteres | 400 | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.85 | `POST /transactions` com categoria de 65 caracteres | 400 (o `PATCH category` devolve 400 no 2.104) | **500** `INTERNAL_SERVER_ERROR` (64 caracteres foi aceito no 2.86) | Alta |
| 2.92 | `POST /transactions` com moeda `REAISREAIS` | 400 | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.62 | `POST /transactions` com `amount: 99999999999999999999` | 400 (a ingestão devolve 400 no 2.138) | **500** `INTERNAL_SERVER_ERROR` | Alta |
| 2.55 | `GET /transactions?page=2147483647&pageSize=100` | 200 com lista vazia ou 400 | **500** `INTERNAL_SERVER_ERROR` | Média |
| 2.63, 2.64 | `POST /transactions` com `amount` 9999999999999999.99 e 1000000000.00 | 400 por limite máximo (a ingestão tem limite) | **201**; o dashboard passou a mostrar `"totalExpenses":10000001000001231.53` (2.118) | Alta |
| 2.89, 2.90, 2.118 | `POST /transactions` com moeda `XYZ` e com `USD` | `XYZ` → 400 (a ingestão rejeita no 2.143); `USD` convertido ou fora do total em reais | **201** nos dois; os 10,00 de cada entram em `totalExpenses` somados com BRL, sem conversão nem separação por moeda | Média |
| 2.61 | `POST /transactions` com `amount: 10.999` | 400 (máx. 2 casas) | **201** devolvendo `"amount":10.999`; na listagem (2.117) o valor gravado é **11.00** — resposta diferente do que ficou salvo e arredondamento silencioso | Média |
| 2.137 | `POST /integrations/events` com `amount: 9.999` | 400 | **201** `Accepted`; transação gravada com `"amount":10.00` (2.153) | Média |
| 2.70 | `POST /transactions` com data `2099-12-31T23:59:59Z` | 400 (a ingestão rejeita data futura no 2.146) | **201**; a transação fica no topo da lista e fora do dashboard do mês | Média |
| 2.71 | `POST /transactions` com data `1900-01-01T00:00:00Z` | 400 | **201** | Baixa |
| 2.72 | `POST /transactions` com `2026-10-05T10:00:00` (sem fuso) | Erro pedindo fuso, ou interpretação documentada | **201** gravando `10:00:00Z`: horário local é tratado como UTC (3 h de diferença para o usuário no Brasil); comportamento diferente da ingestão, que deu 500 (2.133) | Baixa |
| 2.119, 2.120, 2.123 | `GET /integrations/status` antes e depois de criar transação MANUAL, sem ter enviado nenhuma notificação | `totalAccepted:0`, `isActive:false`, `lastEventAtUtc:null` até haver ingestão real | Antes de qualquer evento: `"isActive":true ... "totalAccepted":23`; após 1 manual: `24`; após a 1ª notificação: `25`. **Conta transação manual (inclusive as já excluídas) como evento aceito** e usa a criação manual como `lastEventAtUtc` | Média |
| 2.153 | `GET /integrations/status` depois de 14 eventos recusados (400) e 4 que deram 500 | `totalRejected` > 0 e `lastErrorAtUtc`/`lastErrorMessage` preenchidos | `"lastErrorAtUtc":null,"lastErrorMessage":null ... "totalRejected":0` | Baixa |
| 2.22 | B recategoriza transação criada por A (`PATCH /{id}/category`) | `authorName":"Ana S2"` (como na listagem) | **200** com `"authorName":"Desconhecido"` | Baixa |
| 2.25, 2.26 | B exclui transação criada por A; A exclui transação criada por B | Confirmar a regra: exclusão só pelo autor ou por qualquer membro? | **204** nos dois casos, sem confirmação nem registro de quem excluiu. Se a regra for "qualquer membro pode", está correto | Baixa |
| 2.154 | Excluí a transação criada por notificação e reenviei o mesmo evento | `Duplicate` (o evento já tinha sido processado) | **201** `Accepted`; a transação excluída pelo usuário é recriada | Baixa |
| 2.14 | `GET /transactions?category=transporte` (minúsculas) havendo transação `Transporte` | Encontrar a transação | **200** `{"totalCount":0,...}` — filtro sensível a maiúsculas | Baixa |
| 2.03, 2.122, 2.130, 2.155 | Comparei categorias manuais e automáticas | Lista única de categorias | Categoria manual é texto livre (`Alimentacao`, texto de SQL etc.); a automática usa `Alimentação`, `Saúde`, `Transporte`, `Lazer` e `OUTROS` (esta em maiúsculas). O dashboard agrupa por texto exato, então `Alimentacao` e `Alimentação` viram categorias distintas | Baixa |
| 2.77, 2.78 | `POST /transactions` com descrição `""` e `"   "` | 400 ou normalização para `null` | **201**, gravando `"description":""` e `"description":"   "` | Baixa |
| 2.66 | `POST /transactions` com `amount: "10.50"` (string) | 400 (tipo errado) | **201** com `"amount":10.50` | Baixa |
| 2.141 | `POST /integrations/events` com `bank: "nubank"` | Normalizar para `NUBANK` | **201**; transação gravada com `"bank":"nubank"` (2.153) | Baixa |
| 2.17 | `GET /transactions` com `startDate > endDate` | 400 (como o dashboard no 2.20) | **200** `{"totalCount":0,...}` | Baixa |
| 2.09, 2.10, 2.11, 2.32, 2.34, 2.111 | `PATCH /transactions/{id}`, `PUT /transactions/{id}`, `GET /transactions/{id}` | Editar valor/descrição/data e ler por id | **405** com corpo vazio — não há rota de edição nem de leitura por id; só categoria e meta podem ser alteradas | Média |
| 2.100 | Descrição e `merchant` com `<script>alert(1)</script>` e `<b>` | Sanitizar ou documentar que o cliente deve escapar | **201**; texto gravado e devolvido como veio. Não é falha da API em si, mas exige escape na tela | Baixa |

### Formatos de corpo de erro encontrados (inconsistência — Média)

Foram encontrados **cinco formatos diferentes**, e a mesma regra muda de formato conforme a rota:

1. **Envelope próprio** — `{"code":"INVALID_INPUT","message":"Amount must be greater than zero.","traceId":"0HNP32CO2T64L:00000001"}` (2.59, 2.83, 2.24, 2.33, 2.40 e todos os 500).
2. **ProblemDetails de validação** — `{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"One or more validation errors occurred.","status":400,"errors":{...},"traceId":"00-..."}` (2.18, 2.87, 2.103, 2.135 etc.). O `traceId` tem formato diferente do envelope próprio.
3. **ProblemDetails sem `errors`** — `{"type":"...","title":"Unsupported Media Type","status":415,"traceId":"..."}` (2.99).
4. **Texto puro** — `startDate must not be after endDate` (2.20).
5. **Corpo vazio** — 401 (2.41, 2.42), 405 (2.09 a 2.11, 2.156) e 404 de rota/id não-GUID (2.112 a 2.115, 2.156).

Exemplos da mesma regra com formatos distintos: valor zero é formato 1 em `/transactions` (2.59) e formato 2 em `/integrations/events` (2.136); categoria obrigatória aparece como `"Category is required."` (2.83, formato 1), `"The Category field is required."` (2.87, formato 2) e `"'Category' must not be empty."` (2.103, formato 2).

**Mensagens em inglês:** todas. Nenhuma mensagem de erro em português foi devolvida (ex.: "Transaction not found.", "You must be paired with a partner to access this resource.", "Amount must be greater than zero.", "EventTimestamp cannot be in the future.").

**Mensagens que vazam detalhe interno:**
- Nome de classe e namespace do servidor: `"The JSON value could not be converted to CoupleSync.Api.Contracts.Transactions.CreateManualTransactionRequest. Path: $.amount | LineNumber: 0 | BytePositionInLine: 15."` (2.65, 2.67, 2.68, 2.74, 2.75, 2.98); o mesmo com `...Integrations.IngestNotificationEventRequest` (2.139) e `...PatchTransactionCategoryRequest` (2.106).
- Nome do parâmetro do método: `"request":["The request field is required."]` acompanha todos os erros de desserialização (2.65, 2.96, 2.97).
- Mensagem do desserializador: `"Expected depth to be zero at the end of the JSON payload..."` (2.96, 2.107, 2.148).
- Mensagem contraditória: `"RawNotificationText must not exceed 2048 characters (will be truncated at storage)."` — diz que será truncado, mas a requisição é recusada com 400 (2.149).
- Os 500 **não** vazaram stack trace nem SQL (apenas `"An unexpected error occurred."` + `traceId`).

---

## 3. Funcionalidade não testada (faltou ou não foi implementada)

**Não implementada na API (confirmado por 405 no log):**
- Edição de transação (`PATCH /transactions/{id}` e `PUT /transactions/{id}`): não existe. Não foi possível testar edição de valor, descrição, data, estabelecimento ou moeda (2.09, 2.10). Só categoria e meta são alteráveis.
- Leitura de uma transação por id (`GET /transactions/{id}`): não existe (2.11). A leitura cruzada por C só pôde ser verificada pela listagem (2.31) e pelas rotas de alteração.
- Filtros de listagem por autor/parceiro, banco, origem (`source`), faixa de valor ou texto, e parâmetro de ordenação: não existem (só `page`, `pageSize`, `category`, `startDate`, `endDate`).

**Não testada nesta sessão:**
- Vínculo de transação a uma meta **real** (`PATCH /{id}/goal` com meta existente) e a meta de **outro casal** — só testei `null` e meta inexistente (2.23, 2.24); criar metas está fora do escopo desta sessão.
- Interpretação do `rawNotificationText` em formatos reais de cada banco: a API exige `amount`, `bank` e `eventTimestamp` já estruturados; o texto bruto não gerou descrição nem estabelecimento (2.130), então não há extração no servidor para testar.
- Bancos XP, BTG, SANTANDER, CAIXA e BB na ingestão (testei NUBANK, ITAU, BRADESCO, INTER e C6).
- Limites exatos na ingestão (512/513 em `description` e `merchant`, 2048/2049 em `rawNotificationText`) e limite exato de `merchant` na transação manual (só 5.000).
- Valor máximo exato aceito na ingestão e na transação manual (só valores muito acima e 1 bilhão).
- Transações criadas por OCR (`source` de OCR) e seu reflexo no dashboard.
- Dashboard na virada de mês/fuso (transação às 23h de Brasília do último dia do mês) e com períodos de vários meses.
- Token expirado de verdade (validade de 15 min) e token antigo após troca de casal — só testei token ausente e malformado.
- Concorrência em transação manual (duas edições de categoria ou exclusão + edição simultâneas) e carga/limite de requisições.
- Causa dos HTTP 500: não li logs do contêiner nem código de aplicação; o relatório registra só o que a API respondeu.
