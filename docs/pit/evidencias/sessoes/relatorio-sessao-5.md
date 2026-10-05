# Sessão 5 — Importação de extrato/fatura em PDF (`/api/v1/ocr/*`) e chat de IA (`/api/v1/ai/chat`)

- **Data e hora da execução registrada:** 05/10/2026, 16:17:06 a 16:21:48 (-03:00)
- **Commit testado:** `98e3f64`
- **Ambiente:** API em `http://localhost:5000` (contêiner `couplesync-pit-api-1`), `USE_LOCAL_PDF_PARSER=true`, nenhuma variável `AI_CHAT_ENABLED`/`GEMINI_*` definida no contêiner.
- **Total de casos:** 79 (`### CASO 5.01` a `5.79` em `sessao-5.log`)
- **Evidências:** `sessao-5.sh` (reexecutável; e-mails `s5-<letra>-<data>-<aleatório>@teste.local`), `sessao-5.log` (comando, status HTTP e corpo de cada caso, consultas `SELECT` e trechos de `docker logs`), `sessao-5-arquivos/` (entradas).
- **Cenário:** casal X = Ana (A) + Bruno (B); casal Y = Carla (C); Davi (D) sem casal.

## Como os PDFs foram gerados

`sessao-5-arquivos/gerar_arquivos.py`, com Python 3.14 + `reportlab` 5.0.1 (`pip install --user reportlab`). São PDFs de texto real (fonte Helvetica, `drawString` por linha, sem compressão). **Todos os dados são inventados** (nomes, estabelecimentos, valores, plástico `1234****5678`); os arquivos de teste do repositório foram usados só para entender o formato.

| Arquivo | Conteúdo |
|---|---|
| `a-fatura-inter-valida.pdf` | Cabeçalho "Banco Inter S.A." + 7 débitos distintos + 1 crédito (estorno) |
| `b-fatura-inter-linhas-identicas.pdf` | 7 débitos, sendo 2 linhas idênticas (`04/09/2026 Corrida App Vai Rapido R$ 12,90`) |
| `c-banco-nao-suportado.pdf` | "Cooperativa Financeira Ficticia do Vale" com 3 lançamentos |
| `d-protegido-senha.pdf` | Layout Inter com senha de abertura (`StandardEncryption`) |
| `e-vazio.pdf` | Uma página em branco |
| `f-texto-renomeado.pdf` | Texto puro com extensão `.pdf` |
| `g-imagem.png` | PNG 8x8 |
| `h-11mb.pdf` | PDF (a) + preenchimento até 11.534.336 bytes |
| `i-`, `i2-nubank-*.pdf` | Nubank: 3 lançamentos na mesma página / 1 lançamento por página |
| `n-bb-uma-pagina.pdf`, `o-itau-uma-pagina.pdf` | Banco do Brasil e Itaú: 3 lançamentos na mesma página |
| `j-inter-so-creditos.pdf` | Inter só com créditos |
| `k-corrompido.pdf` | Cabeçalho `%PDF-1.4` + lixo |
| `m-inter-parcelado.pdf` | Inter com "Parcela 03/10", "02/06 lentes" e sufixos D/C |
| `v1..v9-inter-confirmacao.pdf` | Faturas Inter de 3 lançamentos cada, para os casos de confirmação |

Observação sobre o formato: o extrator (PdfPig, `page.Text`) devolve o texto de cada página **sem quebras de linha**. O parser do Inter foi escrito para texto "colado" e por isso foi o escolhido para os arquivos (a) e (b).

## O que testei e funcionou

- **5.01** — Registro de A, B, C, D; criação do casal X (A cria, B entra com o código) e do casal Y.
- **5.02 / 5.03** — Upload de (a) retorna `200 {"uploadId"}`; status `Pending → Ready` em 5,4 s (nas demais importações válidas: 2,1 a 9,3 s; com consulta a cada 0,5 s o estado `Processing` não chegou a ser observado nesta execução).
- **5.04** — `results` de (a): os 7 débitos vieram com data, descrição e valor iguais ao PDF (incluindo `1.234,56` → `1234.56`), `currency=BRL`, `confidence=1`, `duplicateSuspected=false`.
- **5.05 a 5.09** — Confirmação inválida é rejeitada sem alterar o job: lista vazia → `422 INVALID_SELECTION`; `{}` → 400; JSON malformado → 400; sem corpo → 415; índice não numérico → 400.
- **5.10** — `confirm` com `[0..6]` → `200 {"transactionsCreated":7}`; job passa a `Confirmed`.
- **5.11 / 5.12** — `GET /transactions` mostra as 7 transações (`bank="OCR Import"`, `source="OcrImport"`, autor Ana) para A e também para o parceiro B.
- **5.14** — Dashboard com `startDate=2026-09-01&endDate=2026-09-30`: `totalExpenses=1816.96`, `transactionCount=7` (soma exata do PDF). (**5.13**: no período padrão — mês corrente, outubro — o dashboard fica zerado porque os lançamentos são de setembro; comportamento coerente com o período devolvido.)
- **5.15** — Após confirmar: `status=Confirmed`; `results` e novo `confirm` → `409 OCR_JOB_ALREADY_CONFIRMED`; contagem de transações não muda (7).
- **5.16 / 5.17** — Reimportação de (a): os 7 lançamentos vêm com `duplicateSuspected=true`.
- **5.28** — Índice repetido `[0,0,1]` → 2 transações, sem duplicar.
- **5.31** — Categoria com 64 caracteres é aceita; categoria vazia cai em "Outros".
- **5.34** — `confirm` e `results` com o job ainda `Pending` → `409 OCR_JOB_NOT_READY`.
- **5.35** — Parceiro B consulta e confirma o upload feito por A; `categoryOverrides` aplicado ("Lazer"); transações gravadas com B como autor.
- **5.37** — `uploadId` inexistente → `404 OCR_JOB_NOT_FOUND` em `status`, `results` e `confirm`.
- **5.40** — PDF com senha → `Failed`, `errorCode=PDF_ENCRYPTED`, sem reprocessamento (6,5 s).
- **5.41** — PDF vazio → `Failed`, `errorCode=PDF_TOO_SHORT`, sem reprocessamento (7,4 s).
- **5.42** — `.txt` renomeado → `415 UNSUPPORTED_FILE_TYPE` (detecção por magic bytes).
- **5.45, 5.46, 5.48, 5.49** — Upload sem arquivo / sem corpo / 0 bytes / campo com nome errado → 400.
- **5.51** — Nome de arquivo `../../../etc/passwd.pdf`: ignorado; `storage_path` gravado é `uploads/<casal>/<guid>.pdf`.
- **5.52** — PDF declarado como `text/plain` é aceito pelo conteúdo (magic bytes).
- **5.54** — Nubank com um lançamento por página: os 3 lançamentos vêm corretos.
- **5.60** — Upload sem token → 401; usuário sem casal → `403 COUPLE_REQUIRED`.
- **5.61 a 5.64** — Isolamento: C (casal Y) recebe `404 OCR_JOB_NOT_FOUND` em `status`, `results` e `confirm` do upload do casal X; job de X continua `Ready`; C tem 0 transações; A continua enxergando o próprio upload.
- **5.65** — C importa o mesmo arquivo (a) já confirmado por X: nenhum lançamento marcado como duplicata e 7 transações criadas só para o casal Y.
- **5.68 a 5.77** — Validação do chat: mensagem vazia, só espaços, ausente, 10.000 caracteres, 2.001 caracteres, papel `system`, conteúdo vazio, papel nulo, 21 itens de histórico e JSON malformado → 400 com o campo indicado; sem token → 401; sem casal → 403; `GET` → 405.

## O que testei e não funcionou — o que deve ser corrigido

| Caso | O que fiz | O que esperava | O que aconteceu (status + trecho) | Gravidade |
|---|---|---|---|---|
| 5.22 a 5.25 | Importei (b), fatura com duas linhas idênticas no mesmo dia (duas corridas de R$ 12,90), e confirmei todos os índices | As 7 transações gravadas (são duas compras reais), ou erro claro apontando a linha repetida | `results` não marca nenhuma como duplicata. `confirm` → **`500 INTERNAL_SERVER_ERROR` "An unexpected error occurred."**; 0 transações e 0 ingests gravados; job continua `Ready`; repetir dá 500 de novo. Log: `23505: duplicate key value violates unique constraint "IX_transactions_couple_id_fingerprint"`. Único contorno: deixar de fora uma das linhas (5.25 → 6 criadas) — a segunda corrida nunca entra | Alta |
| 5.18 / 5.19 | Reimportei (a) e confirmei os 7 índices marcados como duplicata suspeita | 409 com mensagem clara, ou duplicatas ignoradas com contagem | **`500 INTERNAL_SERVER_ERROR`** (mesma violação 23505 no log); nada gravado; job fica `Ready` para sempre (não existe rota para descartar) | Alta |
| 5.53 | Extrato Nubank com 3 lançamentos em linhas da mesma página (formato dos testes unitários) | 3 candidatos | `Ready` com **1 candidato errado**: `description="Mercado Horta Fresca -R$ 58,2006/09/2026 Transporte Metro Leste -R$ 9,8010/09/2026 Sorveteria Polo Norte"`, `amount=22.00`. Dois lançamentos somem e o valor é o do último, sem erro | Alta |
| 5.55 | Extrato Banco do Brasil, 3 lançamentos na mesma página | 3 candidatos | 1 candidato: `"Quitanda Folha Verde 34,60 D08/09/2026 Barbearia Corte Fino 45,00 D15/09/2026 Lavanderia Bolha Azul"`, `amount=28.90` | Alta |
| 5.56 | Extrato Itaú, 3 lançamentos na mesma página | 3 candidatos | 1 candidato: `"Peixaria Mar Aberto 61,30-09/09 Chaveiro Porta Segura 25,00-16/09 Doceria Mel e Canela"`, `amount=19.75` | Alta |
| 5.59 | Fatura Inter com `07/09/2026 Magazine Casa Bela Parcela 03/10 R$ 99,90` e `09/09/2026 Otica Visao Clara 02/06 lentes R$ 150,00` | 2 candidatos com as datas e descrições do PDF | A compra parcelada de R$ 99,90 **não aparece**; a outra vem como `date=2026-06-02`, `description="lentes"`, `amount=150.00` (o "02/06" da descrição virou a data) | Alta |
| 5.38 | Enviei (c), PDF de banco não suportado | Falha imediata com código/mensagem de formato não reconhecido | `Failed` só após **22,2 s**, com `errorCode="processing_error"` e, no banco, "Internal processing error after max retries.", `retry_count=3`. O log mostra 4 tentativas ("attempt 4/3"). A mensagem específica ("Formato de extrato bancário não reconhecido.") nunca chega ao cliente | Média |
| 5.57 | Enviei PDF corrompido (`%PDF` + lixo) | Falha imediata com mensagem de arquivo inválido | Mesmo comportamento: 24,4 s, 4 tentativas, `processing_error` genérico | Baixa |
| 5.38 a 5.43 (status) | Consultei `status` de jobs com falha | Mensagem legível para o usuário | `status` devolve apenas `{"status":"Failed","errorCode":"PDF_ENCRYPTED",...}`; a mensagem em português gravada em `error_message` não é exposta por nenhuma rota | Média |
| 5.26 | `confirm` com índice inexistente `[99]` | 400/422 e job intacto | **`200 {"transactionsCreated":0}`** e o job vira `Confirmed`; tentar depois `[0,1,2]` → `409 OCR_JOB_ALREADY_CONFIRMED`. Os 3 lançamentos ficam inacessíveis | Média |
| 5.27 | `confirm` com `[-1,0]` | 400/422 pelo índice inválido | `200 {"transactionsCreated":1}`; o índice inválido é ignorado em silêncio | Baixa |
| 5.29 | `categoryOverrides` com o índice 0 repetido | 400 de validação | **`500 INTERNAL_SERVER_ERROR`**; log: `System.ArgumentException: An item with the same key has already been added. Key: 0` | Média |
| 5.30 | `categoryOverrides` com categoria de 65 caracteres | 400 de validação (limite 64) | **`500 INTERNAL_SERVER_ERROR`**; log: `22001 value too long for type character varying(64)`; nada gravado | Média |
| 5.32 | `categoryOverrides` com `"CategoriaQueNaoExiste"` e `"<script>alert(1)</script>"` | Rejeição de categoria fora da lista do casal | `200`; as duas foram gravadas como categoria da transação | Baixa |
| 5.33 | Confirmei só `[0]` de um upload com 3 lançamentos e depois tentei `[1,2]` | Poder confirmar o restante depois, ou aviso de que a confirmação é única | Primeiro → `200` (1 criada); segundo → `409 OCR_JOB_ALREADY_CONFIRMED`. O restante só volta reenviando o PDF. Overrides para índices não selecionados/inexistentes (2 e 77) são ignorados em silêncio | Média |
| 5.36 | Dois `confirm` simultâneos no mesmo upload | Um 200 e um 409 | Um `200 {"transactionsCreated":3}` e um **`500 INTERNAL_SERVER_ERROR`**; não houve duplicação (3 linhas no banco) | Baixa |
| 5.39 | `results` e `confirm` de um job `Failed` | Mensagem dizendo que a importação falhou | `409 OCR_JOB_NOT_READY` "OCR processing is not complete yet." — enganosa, o processamento já terminou | Baixa |
| 5.43 | Upload de PNG | Recusa no upload (o parser local não trata imagem) | Upload `200`; 6,1 s depois `Failed` com `IMAGE_NOT_SUPPORTED`. Enquanto isso, o erro 415 de 5.42 diz "Accepted file types: JPEG, PNG, PDF." | Baixa |
| 5.44 | Upload de 11 MB | `413` com `FILE_TOO_LARGE` "File must be 10 MB or less." (o que o controller declara) | **`400`** `application/problem+json`: "Failed to read the request form. Request body too large. The max request body size is 10485760 bytes." | Baixa |
| 5.50 | Upload com dois arquivos no campo `file` | 400, ou dois jobs | `200` com um único `uploadId`; só um job criado (16 → 17); o segundo arquivo é descartado sem aviso | Baixa |
| 5.04 / 5.59 | (a) tem um estorno de R$ 30,00; (m) tem um depósito com sufixo C | Crédito listado, ou aviso de que créditos são ignorados | Créditos somem de `results` sem nenhuma indicação | Baixa |
| 5.58 | Fatura Inter só com créditos; depois `confirm [0]` | `Failed` com "nenhuma transação encontrada" | `Ready` com `{"candidates":[]}`; `confirm [0]` → `200 {"transactionsCreated":0}` e job `Confirmed` | Baixa |
| 5.04 / 5.11 | Conferi a categoria sugerida | Alguma sugestão por lançamento | `suggestedCategory=null` em todos; as 7 transações ficaram em "Outros" (sem IA configurada não há classificador) | Baixa |
| 5.66 / 5.67 / 5.72 / 5.78 | `POST /ai/chat` com mensagem válida, sem chave configurada | Resposta dizendo claramente que o assistente está desabilitado (ex.: 503 e texto em português) | **`404`** `{"code":"AI_CHAT_DISABLED","message":"AI Chat is not available."}` — em inglês, sem `traceId`, e 404 se confunde com rota inexistente. A validação (400) roda antes dessa checagem | Baixa |
| Formato de erro (5.05, 5.06, 5.09, 5.37, 5.44, 5.47, 5.60, 5.77) | Comparei os corpos de erro | Um formato único, em português, sem detalhe interno | Três formatos: (1) `{"code","message","traceId"}` (422, 409, 500, 403; `content-type: application/json` sem charset); (2) `{"code","message"}` sem `traceId` (404, 415, `FILE_REQUIRED`, `AI_CHAT_DISABLED`); (3) `application/problem+json` do ASP.NET (400, 415). Corpo vazio em 401, 405, 404 de id não-GUID e 415 do upload com JSON. Mensagens da API em inglês. Vazamento de detalhe interno: "The JSON value could not be converted to System.Int32. Path: $.selectedIndices[0] \| LineNumber: 0 \| BytePositionInLine: 25." e "The max request body size is 10485760 bytes." | Baixa |

Estado final dos jobs do casal X (caso 5.79): 11 `Confirmed`, 7 `Failed` (4 com `processing_error`) e 8 em `Ready`. Nenhum ficou em `Pending`/`Processing`. Entre os `Ready` está a reimportação de (a) com 7 duplicatas, que não pode ser confirmada (500) nem descartada.

## Funcionalidade não testada (faltou ou não foi implementada)

- **Alterar valor, descrição ou data na confirmação — não implementado.** O contrato `ConfirmRequest` só tem `selectedIndices` e `categoryOverrides {index, category}`. No caso 5.32 enviei `amount`, `description`, `date`, `amountOverrides` e `descriptionOverrides`: resposta `200` e os valores gravados continuaram os do PDF (17,11 / "Emporio Gaivota Um" / 07/08/2026).
- **Descartar/cancelar uma importação e listar importações do casal — não existe rota** (só `upload`, `status`, `results`, `confirm`).
- **Chat de IA com resposta real — não testado**: desabilitado no ambiente. Ficaram sem teste a qualidade da resposta, o limite de 30 requisições/hora (`CHAT_RATE_LIMITED`), o uso do histórico e o isolamento dos dados do casal no contexto enviado ao modelo.
- **Sugestão de categoria por IA — não testada**: sem IA, o classificador devolve sempre `null`.
- **Caminho Azure Document Intelligence e cota (`quota_exhausted`, `quotaResetDate`) — não testado**: o ambiente usa o parser local.
- **Imagens (JPEG/PNG) com OCR — não implementado no parser local** (`IMAGE_NOT_SUPPORTED`); JPEG não foi enviado.
- **Parsers de Caixa, Santander e Mercantil — não executados.** Usam a mesma expressão do Banco do Brasil (5.55), mas não há caso no log.
- **Extratos reais dos bancos — não testados.** Todos os PDFs são sintéticos; o layout real pode se comportar de outra forma.
- **Limite exato de 10 MB** — só testei 11 MB.
- **Job interrompido no meio do processamento (reinício da API com job em `Processing`)** — não testado, pois não era permitido reiniciar contêineres.
- **PDF apenas com senha de permissões (abre sem senha)** — não testado; o arquivo (d) tem senha de abertura.
