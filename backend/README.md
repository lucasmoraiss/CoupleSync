# CoupleSync — API (back-end)

API REST em .NET 8 do CoupleSync, aplicativo de finanças compartilhadas para casais e famílias. Cuida de autenticação, grupos ("casais"), transações, captura de notificações bancárias enviadas pelo app Android, importação de extratos em PDF, rendas, metas, fluxo de caixa, relatórios, alertas por push e, opcionalmente, um assistente de IA.

## O que a API faz

- **Autenticação por JWT** com refresh token rotacionado a cada uso.
- **Isolamento por grupo**: o `couple_id` vem do token; os controllers de dados exigem o filtro `[RequireCouple]` e as consultas filtram pelo grupo.
- **Limite de tentativas** em login, cadastro e entrada em grupo (HTTP 429).
- **Transações**: lançamento manual, troca de categoria, vínculo com meta e exclusão.
- **Captura de notificações**: recebe do app eventos já estruturados (banco, valor, moeda, data/hora, estabelecimento), descarta duplicatas e cria a transação com categoria sugerida por regras.
- **Importação de extrato em PDF**: o arquivo é processado por um job em segundo plano, que devolve candidatos para o usuário revisar e confirmar.
- **Rendas, orçamento mensal, metas, fluxo de caixa (30 ou 90 dias) e relatórios**.
- **Alertas** enviados por Firebase Cloud Messaging (opcional: sem credenciais, o envio é pulado e registrado em log).
- **Assistente de IA** (opcional, desligado por padrão) usando a API do Gemini.

## Requisitos

- .NET 8 SDK
- PostgreSQL (o CI usa a versão 16)
- Opcionais: projeto Firebase (push) e chave da API do Gemini (assistente e categorização automática)

## Como rodar

### 1. Variáveis mínimas

A API **não sobe** sem uma chave JWT válida e sem a conexão com o banco. O `appsettings.json` versionado traz `Jwt:Secret` e `ConnectionStrings:DefaultConnection` vazios de propósito.

```bash
# Gere a sua chave; não reutilize exemplos de documentação.
export JWT__SECRET="$(openssl rand -hex 32)"
export DATABASE_URL="Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=<sua-senha>"
```

No PowerShell 7:

```powershell
$env:JWT__SECRET = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:DATABASE_URL = "Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=<sua-senha>"
```

Como alternativa, crie `src/CoupleSync.Api/appsettings.Development.json` (ignorado pelo Git) com `Jwt:Secret` e `ConnectionStrings:DefaultConnection`.

### 2. Subir

```bash
cd backend
dotnet restore
dotnet run --project src/CoupleSync.Api --launch-profile http
```

A API escuta em `http://localhost:5000` (perfil `http` de `Properties/launchSettings.json`). Ao iniciar, ela **aplica as migrações pendentes** e carrega as regras de categorização; não é preciso rodar `dotnet ef database update` à mão.

### 3. Conferir

```bash
curl http://localhost:5000/health/ready     # 200 quando o banco responde

curl -X POST http://localhost:5000/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"ana@example.com","name":"Ana","password":"<senha-de-8-ou-mais>"}'
```

### Com Docker Compose

Na raiz do repositório há um `docker-compose.yml` que sobe a API (porta 5000 no host) e um PostgreSQL. Copie `.env.example` para `.env`, defina `JWT__SECRET` e rode `docker compose up --build`.

## Variáveis de ambiente

Estas são as chaves que o código lê. Nomes com `__` seguem a convenção do .NET para seções (`JWT__SECRET` equivale a `Jwt:Secret`); maiúsculas e minúsculas não fazem diferença.

| Variável | Obrigatória | Padrão | Para que serve |
|---|---|---|---|
| `DATABASE_URL` | Sim (ou `ConnectionStrings__DefaultConnection`) | — | Conexão PostgreSQL. Aceita pares `Host=...;Port=5432;Database=...;Username=...;Password=...` ou URI `postgresql://usuario:senha@host/banco`. Para hosts do Neon, TLS e limites de pool são aplicados automaticamente. |
| `JWT__SECRET` | Sim | vazio | Chave de assinatura dos tokens. Mínimo de 32 caracteres; a API recusa iniciar se estiver vazia, curta ou igual ao marcador de documentação. Gere com `openssl rand -hex 32`. |
| `JWT__ISSUER` | Não | `CoupleSync` | Emissor do token. |
| `JWT__AUDIENCE` | Não | `CoupleSync.Mobile` | Audiência do token. |
| `JWT__ACCESSTOKENTTLMINUTES` | Não | `15` | Validade do access token, em minutos. |
| `JWT__REFRESHTOKENTTLDAYS` | Não | `7` | Validade do refresh token, em dias. |
| `RateLimiting__Auth__PermitLimit` / `RateLimiting__Auth__WindowSeconds` | Não | `5` / `60` | Limite por IP em `POST /auth/login` e `POST /auth/register` (um contador por endpoint). |
| `RateLimiting__CoupleJoin__PermitLimit` / `RateLimiting__CoupleJoin__WindowSeconds` | Não | `5` / `60` | Limite por usuário em `POST /couples/join`. |
| `ForwardedHeaders__TrustAllProxies` | Não | `false` | `true` faz a API aceitar `X-Forwarded-For` de qualquer origem. Use só quando o contêiner é alcançável apenas pelo proxy da plataforma. |
| `ForwardedHeaders__KnownProxies__0`, `ForwardedHeaders__KnownNetworks__0` | Não | vazio | IPs e faixas CIDR de proxies confiáveis (índices `__0`, `__1`, ...). |
| `ForwardedHeaders__ForwardLimit` | Não | `1` | Quantos saltos de `X-Forwarded-For` são considerados. |
| `USE_LOCAL_PDF_PARSER` | Não | `true` | `true` usa o parser local de PDF (PdfPig). `false` usa o Azure Document Intelligence. |
| `Storage__BasePath` | Não | `./uploads` no diretório de trabalho | Pasta onde o arquivo enviado fica até ser processado. |
| `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT`, `AZURE_DOCUMENT_INTELLIGENCE_KEY` | Só com `USE_LOCAL_PDF_PARSER=false` | — | Credenciais do provedor de OCR alternativo. |
| `Fcm__ProjectId`, `Fcm__CredentialJson` | Não | vazio | Projeto Firebase e JSON da conta de serviço. Sem eles, o envio de push é pulado. |
| `AI_CHAT_ENABLED` | Não | desligado | `true` habilita o assistente e a categorização automática de candidatos do extrato. |
| `GEMINI_API_KEY` | Só com IA ligada | vazio | Chave da API do Gemini. |
| `GEMINI_MODEL` | Não | `gemini-2.0-flash` | Modelo usado. |
| `ASPNETCORE_URLS` | Não | — | Endereço de escuta. O `Dockerfile` fixa `http://+:8080`. |

**Atrás de proxy reverso.** O limite de tentativas usa o IP do cliente. Sem configurar `ForwardedHeaders`, a API enxerga o IP do proxy e todos os usuários passam a dividir o mesmo contador. Informe os proxies em `KnownProxies`/`KnownNetworks` ou, em plataformas cujo proxy não tem endereço fixo, use `ForwardedHeaders__TrustAllProxies=true`. Veja `src/CoupleSync.Api/RateLimiting/RateLimitingSetup.cs`.

## Endpoints

Todos sob `/api/v1`, exceto os de saúde. Fora os de `auth` e saúde, todos exigem `Authorization: Bearer <access token>`. Os marcados com **G** exigem também que o usuário já pertença a um grupo (senão, 403 `COUPLE_REQUIRED`).

### Autenticação — `AuthController`

| Método | Rota | O que faz |
|---|---|---|
| POST | `/auth/register` | Cria a conta (e-mail, nome, senha de 8+ caracteres) e devolve os tokens. Limite: 5/min por IP. |
| POST | `/auth/login` | Autentica e devolve access token e refresh token. Limite: 5/min por IP. |
| POST | `/auth/refresh` | Troca um refresh token válido por um novo par de tokens. |

### Grupo ("casal") — `CouplesController`

| Método | Rota | O que faz |
|---|---|---|
| POST | `/couples` | Cria o grupo, gera o código de convite de 6 caracteres e devolve um novo access token. |
| POST | `/couples/join` | Entra em um grupo pelo código. Limite: 5/min por usuário. |
| GET | `/couples/me` | Dados do grupo do usuário: código de convite e membros. |

Um grupo aceita mais de dois membros. Não há endpoints para sair do grupo, remover membro ou trocar o código, e cada usuário pertence a um único grupo.

### Transações — `TransactionsController` (G)

| Método | Rota | O que faz |
|---|---|---|
| GET | `/transactions` | Lista paginada (`page`, `pageSize` até 100), com filtros opcionais `category`, `startDate`, `endDate`. |
| POST | `/transactions` | Lança uma transação manual. |
| PATCH | `/transactions/{id}/category` | Troca a categoria. |
| PATCH | `/transactions/{id}/goal` | Vincula a transação a uma meta. |
| DELETE | `/transactions/{id}` | Exclui a transação. |

### Captura de notificações — `IntegrationsController` (G)

| Método | Rota | O que faz |
|---|---|---|
| POST | `/integrations/events` | Recebe um evento de despesa extraído de uma notificação bancária. |
| GET | `/integrations/status` | Situação da captura: último evento, último erro e contadores de aceitos, duplicados e rejeitados. |

### Importação de extrato — `OcrController` (G)

| Método | Rota | O que faz |
|---|---|---|
| POST | `/ocr/upload` | Recebe o arquivo (até 10 MB) e cria o job de importação. |
| GET | `/ocr/{uploadId}/status` | Situação do job e código de erro, se houver. |
| GET | `/ocr/{uploadId}/results` | Candidatos extraídos, com sugestão de categoria e marcação de possível duplicata. |
| POST | `/ocr/{uploadId}/confirm` | Cria as transações selecionadas. Aceita `categoryOverrides` e `candidateEdits` (correção de descrição e valor); devolve `transactionsCreated` e `duplicatesSkipped`. |

O endpoint de upload reconhece JPEG, PNG e PDF pelos primeiros bytes do arquivo, mas o parser local (padrão) só processa PDF: uma imagem termina com o erro `IMAGE_NOT_SUPPORTED`. O arquivo enviado é apagado do disco ao fim do processamento, com sucesso ou com falha definitiva.

### Rendas — `IncomesController` (G)

| Método | Rota | O que faz |
|---|---|---|
| POST | `/incomes` | Cria uma fonte de renda para um mês (`YYYY-MM`). |
| GET | `/incomes/current` | Rendas do mês corrente: pessoais, dos demais membros e compartilhadas. |
| GET | `/incomes/{month}` | Rendas de um mês específico. |
| PUT | `/incomes/{id}` | Altera uma fonte (dono ou compartilhada). |
| DELETE | `/incomes/{id}` | Remove uma fonte (dono ou compartilhada). |

### Orçamento — `BudgetController` (G)

| Método | Rota | O que faz |
|---|---|---|
| POST | `/budgets` | Cria ou atualiza o plano de um mês. |
| GET | `/budgets/current` | Plano do mês corrente. |
| GET | `/budgets/{month}` | Plano de um mês (`YYYY-MM`). |
| PUT | `/budgets/{planId}/allocations` | Substitui as alocações por categoria do plano. |
| PATCH | `/budgets/income` | Atualiza a renda bruta do mês corrente (cria o plano se não existir). |

O app atual não tem tela para as alocações de orçamento; esses endpoints são usados pelos testes e pelo contexto do assistente.

### Metas — `GoalsController` (G)

| Método | Rota | O que faz |
|---|---|---|
| POST | `/goals` | Cria uma meta (título, valor alvo, prazo de hoje em diante). |
| GET | `/goals` | Lista as metas (`includeArchived=true` inclui as arquivadas). |
| GET | `/goals/progress-summary` | Resumo de progresso das metas. |
| GET | `/goals/{id}` | Detalhe de uma meta. |
| PATCH | `/goals/{id}` | Altera título, descrição, valor alvo, valor atual ou prazo. |
| DELETE | `/goals/{id}` | Exclui a meta. |
| DELETE | `/goals/{id}/archive` | Arquiva a meta. |
| GET | `/goals/{id}/progress` | Progresso de uma meta. |

### Demais

| Método | Rota | Controller | O que faz |
|---|---|---|---|
| GET | `/dashboard` (G) | `DashboardController` | Total de gastos do período, gasto por membro e por categoria (`startDate`, `endDate` opcionais). |
| GET | `/cashflow?horizon=30\|90` (G) | `CashFlowController` | Gasto histórico, média diária e projeção para 30 ou 90 dias. |
| GET | `/reports/spending-by-category?months=N` (G) | `ReportsController` | Gastos por categoria nos últimos N meses (1 a 60). |
| GET | `/reports/monthly-trends?months=N` (G) | `ReportsController` | Gasto mês a mês (1 a 60). |
| POST | `/devices/token` (G) | `NotificationsController` | Registra o token de push do aparelho (só `android`). |
| GET | `/notifications/settings` (G) | `NotificationsController` | Preferências de alerta do usuário. |
| PUT | `/notifications/settings` (G) | `NotificationsController` | Atualiza as preferências de alerta. |
| POST | `/ai/chat` (G) | `ChatController` | Pergunta ao assistente. Devolve 404 `AI_CHAT_DISABLED` quando a IA está desligada; limite de 30 mensagens por hora por grupo. |

### Saúde (sem autenticação)

| Método | Rota | O que faz |
|---|---|---|
| GET | `/health` | Responde `healthy` com data e hora. |
| GET | `/health/live` | Confirma que o processo está de pé (não consulta o banco). |
| GET | `/health/ready` | Confirma que o banco responde. |

## Estrutura do projeto

```
backend/
├── CoupleSync.sln
├── Dockerfile
├── src/
│   ├── CoupleSync.Api/              # Controllers, contratos, validadores, filtros
│   │   ├── Controllers/
│   │   ├── Contracts/
│   │   ├── Validators/              # FluentValidation
│   │   ├── Filters/                 # RequireCoupleAttribute
│   │   ├── Middleware/              # GlobalExceptionMiddleware
│   │   ├── RateLimiting/            # limites e cabeçalhos de proxy
│   │   ├── Security/                # JwtSecretGuard
│   │   ├── Health/
│   │   └── Program.cs
│   ├── CoupleSync.Application/      # Casos de uso por módulo (Auth, Couple, Transactions,
│   │                                # NotificationCapture, OcrImport, Income, Budget, Goals,
│   │                                # CashFlow, Dashboard, Reports, Notification, AiChat)
│   ├── CoupleSync.Domain/           # Entidades, interfaces e objetos de valor
│   └── CoupleSync.Infrastructure/   # EF Core, migrações, segurança, jobs e integrações
│       ├── Persistence/
│       ├── Migrations/
│       ├── Security/                # BCryptPasswordHasher, JwtTokenService, HttpContextCoupleContext
│       ├── BackgroundJobs/          # OcrBackgroundJob, NotificationDispatcherJob
│       └── Integrations/            # Fcm, Gemini, LocalPdfParser, AzureDocumentIntelligence, Storage
└── tests/
    ├── CoupleSync.UnitTests/
    ├── CoupleSync.IntegrationTests/
    └── CoupleSync.E2ETests/
```

## Testes

```bash
# na raiz do repositório
export DATABASE_URL="Host=localhost;Port=5432;Database=couplesync_test;Username=postgres;Password=postgres"
dotnet test backend/CoupleSync.sln
```

São **588 testes**: 411 de unidade, 176 de integração e 1 de ponta a ponta. Os testes de integração e o de ponta a ponta sobem a API em memória e trocam o banco por SQLite; `DATABASE_URL` precisa estar definida porque a inicialização da API exige uma conexão configurada.

Para rodar um projeto só:

```bash
dotnet test backend/tests/CoupleSync.UnitTests/CoupleSync.UnitTests.csproj
```

## Segurança

- **Isolamento por grupo**: o identificador do grupo vem do claim `couple_id` do token (`HttpContextCoupleContext`), é exigido por `Filters/RequireCoupleAttribute.cs` e aplicado nas consultas. Ver [ADR-0002](../docs/adr/0002-autorizacao-por-casal.md).
- **Senhas** guardadas como hash BCrypt (`Infrastructure/Security/BCryptPasswordHasher.cs`).
- **Refresh tokens** guardados como hash SHA-256 e rotacionados a cada uso.
- **Segredos** (chave JWT, senha do banco, credenciais do Firebase e do Gemini) vêm de variáveis de ambiente. O `appsettings.json` versionado não contém segredo, e `appsettings.Development.json` é ignorado pelo Git.
- **Sem credenciais bancárias**: o app lê notificações no Android e envia só dados estruturados da despesa. O contrato de `POST /integrations/events` ainda aceita os campos opcionais `description` e `rawNotificationText`, que o app não envia.

## Problemas comuns

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| `Invalid JWT secret configuration` ao iniciar | `JWT__SECRET` vazia ou com menos de 32 caracteres | Gere uma chave nova e defina a variável. |
| `Database connection string is missing` | Nem `DATABASE_URL` nem `ConnectionStrings:DefaultConnection` definidas | Defina `DATABASE_URL`. |
| `/health/ready` não devolve 200 | Banco inacessível | Confira host, porta, usuário e senha da conexão. |
| HTTP 429 em login, cadastro ou entrada em grupo | Limite de tentativas atingido | Aguarde o tempo indicado no cabeçalho `Retry-After`. Atrás de proxy, configure `ForwardedHeaders`. |
| Log `FCM is not configured` | `Fcm__ProjectId` ou `Fcm__CredentialJson` vazios | Esperado em desenvolvimento; os alertas não são enviados por push. |

## Implantação

Veja [docs/deployment/DEPLOY-GUIDE.md](../docs/deployment/DEPLOY-GUIDE.md). As decisões de arquitetura estão em [docs/adr/](../docs/adr/README.md).
