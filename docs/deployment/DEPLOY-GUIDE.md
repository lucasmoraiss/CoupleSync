# CoupleSync — Guia Definitivo de Deployment e Operação

> **Atualizado:** 19 de abril de 2026 | **Status:** Backend validado e rodando em produção

---

## 1. Visão Geral da Arquitetura

```
┌─────────────────┐        HTTPS         ┌──────────────────────────────────┐
│  APK Android    │ ──────────────────→   │  Azure App Service F1 (Linux)   │
│  (Expo/EAS)     │                       │  couplesyncapi.azurewebsites.net│
└─────────────────┘                       │                                │
                                          │  .NET 8 Web API                │
                                          │  ├─ OCR local (PdfPig)         │
                                          │  ├─ AI (Gemini Flash)          │
                                          │  ├─ Push (FCM)                 │
                                          │  └─ PDFs deletados após parse  │
                                          └──────────┬─────────────────────┘
                                                     │
                          ┌──────────────────────────┼──────────────────────┐
                          │                          │                      │
                   ┌──────▼──────┐         ┌────────▼────────┐   ┌────────▼────────┐
                   │ Neon.tech   │         │ Firebase (FCM)  │   │ Google Gemini   │
                   │ PostgreSQL  │         │ Push + Distrib. │   │ AI Chat + OCR   │
                   │ Free tier   │         │ Free            │   │ Free tier       │
                   └─────────────┘         └─────────────────┘   └─────────────────┘
```

**Todos os serviços são 100% free tier. Custo total: R$ 0,00.**

---

## 2. Serviços e Credenciais

### 2.1 Azure App Service (Backend)

| Campo | Valor |
|---|---|
| App Name | `CoupleSyncApi` |
| URL | `https://couplesyncapi.azurewebsites.net` |
| Resource Group | `CoupleSync` |
| Plan | `ASP-CoupleSync-8eec` (F1 Free) |
| OS | Linux |
| Runtime | .NET 8.0 |
| Region | Brazil South |
| Startup Command | `dotnet CoupleSync.Api.dll` |

### 2.2 Neon PostgreSQL (Banco de Dados)

| Campo | Valor |
|---|---|
| Host (pooler) | `ep-empty-violet-acixvsij-pooler.sa-east-1.aws.neon.tech` |
| Database | `couplesync` |
| User | `neondb_owner` |
| Free Tier | 10 GB storage, auto-suspend após inatividade |

**Nota:** O compute do Neon suspende automaticamente após inatividade. A primeira request pode demorar ~3s (cold start do banco).

### 2.3 Firebase

| Campo | Valor |
|---|---|
| Project ID | `couplesync-78e08` |
| Android package | `com.couplesync.app` |
| App ID | `1:247947489218:android:8eb748248b90016af4b05b` |
| Serviços usados | Cloud Messaging (push), App Distribution (APK) |

### 2.4 Google Gemini (AI)

| Campo | Valor |
|---|---|
| Modelo | `gemini-2.5-flash` |
| Free tier | 15 req/min, 1M tokens/dia |
| Usado para | AI Chat, auto-categorização de transações OCR |

---

## 3. Ambientes: Desenvolvimento vs Produção

### Desenvolvimento (seu PC)

```bash
# Terminal 1: Backend (localhost:5000)
cd backend/src/CoupleSync.Api
dotnet run
# Usa: appsettings.Development.json → PostgreSQL local (localhost:5432/couplesync_dev)

# Terminal 2: Mobile (Expo Dev Server)
cd mobile
npx expo start --android
# Usa: .env → EXPO_PUBLIC_API_BASE_URL=http://<SEU_IP_LOCAL>:5000
```

### Produção (Azure)

O App Service **NÃO usa** `appsettings.Development.json`. Ele lê:
1. **Environment variables** configuradas no Azure Portal — **estas ganham**
2. `appsettings.json` — apenas placeholders, nunca segredos

---

## 4. Variáveis de Ambiente (Azure App Service)

Configuradas em: Azure Portal → App Service → **Configuration** → **Application settings**

| Variável | Como o código lê | Observação |
|---|---|---|
| `DATABASE_URL` | `Environment.GetEnvironmentVariable("DATABASE_URL")` | Connection string completa do Neon |
| `Jwt__Secret` | .NET mapeia → `Jwt:Secret` | String aleatória 32+ chars |
| `Jwt__Issuer` | .NET mapeia → `Jwt:Issuer` | Valor: `CoupleSync` |
| `Jwt__Audience` | .NET mapeia → `Jwt:Audience` | Valor: `CoupleSync.Mobile` |
| `Fcm__ProjectId` | .NET mapeia → `Fcm:ProjectId` | Valor: `couplesync-78e08` |
| `Fcm__CredentialJson` | .NET mapeia → `Fcm:CredentialJson` | JSON completo do firebase-admin-key |
| `GEMINI_API_KEY` | `configuration["GEMINI_API_KEY"]` | API key do Google AI Studio |
| `GEMINI_MODEL` | `configuration["GEMINI_MODEL"]` | Valor: `gemini-2.5-flash` |
| `AI_CHAT_ENABLED` | `configuration["AI_CHAT_ENABLED"]` | Valor: `true` |
| `USE_LOCAL_PDF_PARSER` | `configuration.GetValue<bool>(...)` | Valor: `true` |

### Por que alguns usam `__` e outros não?

- **`Jwt__Secret`**, **`Fcm__ProjectId`**: O .NET usa `__` para representar hierarquia JSON (`Jwt.Secret`, `Fcm.ProjectId`)
- **`DATABASE_URL`**, **`GEMINI_API_KEY`**: O código lê diretamente como variável flat

---

## 5. Deploy Manual do Backend

```powershell
cd backend

# 1. Compilar
dotnet publish src/CoupleSync.Api/CoupleSync.Api.csproj -c Release -o ./publish

# 2. Zipar
if (Test-Path ./deploy.zip) { Remove-Item ./deploy.zip }
Compress-Archive -Path ./publish/* -DestinationPath ./deploy.zip -Force

# 3. Deploy via Azure CLI
az webapp deploy --resource-group CoupleSync --name CoupleSyncApi --src-path ./deploy.zip --type zip
```

---

## 6. CI/CD — GitHub Actions

### Pipelines existentes

| Workflow | Arquivo | Trigger | O que faz |
|---|---|---|---|
| **CI** | `ci.yml` | Push/PR em `main` | Build + 391 testes + TypeScript check |
| **Deploy** | `deploy.yml` | CI verde em `main` OU manual | Publica no App Service |
| **Mobile APK** | `mobile-apk.yml` | Tag `v*` OU manual | EAS Build → Firebase App Distribution |

### GitHub Secrets necessários

Configurar em: GitHub → repo → Settings → Secrets and variables → Actions

| Secret | Necessário para | Como obter |
|---|---|---|
| `AZURE_PUBLISH_PROFILE` | deploy.yml | Conteúdo XML do `publish-profile.xml` (já salvo localmente) |
| `EXPO_TOKEN` | mobile-apk.yml | [expo.dev](https://expo.dev/) → Settings → Access Tokens → Create |
| `FIREBASE_TOKEN` | mobile-apk.yml | `firebase login:ci` no terminal → copie o token |
| `FIREBASE_APP_ID` | mobile-apk.yml | `1:247947489218:android:8eb748248b90016af4b05b` (do `google-services.json`) |

### Fluxo após configuração:

```
git push main → CI (build + testes) → Deploy (App Service) → Health check
git tag v1.0.0 → Mobile APK (EAS Build) → Firebase App Distribution
```

---

## 7. Database — Migrations

### Aplicar no Neon (produção)
```powershell
cd backend/src/CoupleSync.Api
$env:DATABASE_URL = "postgresql://neondb_owner:SENHA@HOST/couplesync?sslmode=require"
dotnet ef database update --project ../CoupleSync.Infrastructure
```

### Criar nova migration (após mudanças em entidades)
```powershell
dotnet ef migrations add NomeDaMigration --project ../CoupleSync.Infrastructure
```

**Importante:** Sempre aplique migrations no Neon ANTES de fazer deploy do backend.

---

## 8. Deploy do Mobile (APK)

### Build
```bash
cd mobile
eas login          # primeira vez
eas build --platform android --profile preview
# Aguarde ~10 min, copie o link do APK
```

### Distribuir
```bash
firebase appdistribution:distribute CAMINHO_APK \
  --app 1:247947489218:android:8eb748248b90016af4b05b \
  --release-notes "Pilot v1.0"
```

Ou envie o APK diretamente via WhatsApp/email para os testers.

---

## 9. Health Endpoints

| Endpoint | Verifica |
|---|---|
| `GET /health` | App rodando |
| `GET /health/live` | Liveness probe |
| `GET /health/ready` | DB acessível |

---

## 10. Troubleshooting

| Problema | Solução |
|---|---|
| `/health` → 404 | App crashou. Ver logs: `az webapp log tail -g CoupleSync -n CoupleSyncApi` |
| Página padrão do Azure | Startup command: `az webapp config set -g CoupleSync -n CoupleSyncApi --startup-file "dotnet CoupleSync.Api.dll"` |
| "Invalid JWT secret" | Verificar `Jwt__Secret` tem 32+ chars |
| DB connection error | Verificar `DATABASE_URL` e senha do Neon |
| Cold start ~30s | Normal no F1. Primeira request após inatividade é lenta |
| APK não conecta | Verificar `EXPO_PUBLIC_API_BASE_URL` em `eas.json`, rebuild APK |

---

## 11. Segurança — NUNCA Commitar

Protegido pelo `.gitignore`:
- `mobile/google-services.json`
- `**/appsettings.Development.json`
- `backend/publish-profile.xml`
- `backend/publish/` e `deploy.zip`
- `.env` e `.env.*`

---

## 12. Status Atual (19/04/2026)

| Item | Status |
|---|---|
| Backend deployado e rodando | ✅ |
| DB Neon com schema (12 migrations) | ✅ |
| Registro de usuário funcional | ✅ |
| Health + DB check | ✅ |
| 10 env vars configuradas | ✅ |
| CI workflow (ci.yml) | ✅ Existente |
| CD backend (deploy.yml) | ⚠️ Precisa secret `AZURE_PUBLISH_PROFILE` |
| Mobile APK build | ❌ Pendente |
| CD mobile (mobile-apk.yml) | ⚠️ Precisa secrets Expo/Firebase |
# CoupleSync — Guia Definitivo de Deployment e Operação

> **Atualizado:** 18 de abril de 2026  
> **Escopo:** 5-10 usuários pilotos, custo R$ 0,00 permanente

---

## 1. Arquitetura Final

```
┌─────────────────┐        HTTPS         ┌──────────────────────────────────┐
│  APK Android    │ ──────────────────→   │  Azure App Service F1 (Linux)   │
│  (Expo/EAS)     │                       │  couplesyncapi.azurewebsites.net│
└─────────────────┘                       │                                │
                                          │  .NET 8 Web API                │
                                          │  ├─ OCR local (PdfPig)         │
                                          │  ├─ AI (Gemini Flash)          │
                                          │  ├─ Push (FCM)                 │
                                          │  └─ PDFs deletados após parse  │
                                          └──────────┬─────────────────────┘
                                                     │
                          ┌──────────────────────────┼──────────────────────┐
                          │                          │                      │
                   ┌──────▼──────┐         ┌────────▼────────┐   ┌────────▼────────┐
                   │ Neon.tech   │         │ Firebase (FCM)  │   │ Google Gemini   │
                   │ PostgreSQL  │         │ Push + Distrib. │   │ AI Chat + OCR   │
                   │ 10GB free   │         │ Unlimited free  │   │ 15 RPM free     │
                   └─────────────┘         └─────────────────┘   └─────────────────┘
```

**Custo total: R$ 0,00** — todos os serviços são 100% free tier.

---

## 2. Como os Ambientes Funcionam

### Ambiente de Desenvolvimento (seu PC)

| Componente | Onde roda | Configuração |
|---|---|---|
| Backend .NET 8 | `dotnet run` (localhost:5000) | `appsettings.Development.json` |
| PostgreSQL | Docker local (localhost:5432) | `couplesync_dev` database |
| Mobile | Expo Dev Server (`npx expo start`) | `.env` com `EXPO_PUBLIC_API_BASE_URL=http://<SEU_IP>:5000` |

**Para rodar o dev:**
```bash
# Terminal 1: Backend
cd backend/src/CoupleSync.Api
dotnet run

# Terminal 2: Mobile  
cd mobile
npx expo start --android
```

O backend usa `appsettings.Development.json` automaticamente quando `ASPNETCORE_ENVIRONMENT=Development` (padrão do `dotnet run`).

### Ambiente de Produção (Azure)

| Componente | Onde roda | Configuração |
|---|---|---|
| Backend .NET 8 | Azure App Service F1 | Environment variables no Azure Portal |
| PostgreSQL | Neon.tech (cloud) | `DATABASE_URL` env var |
| Mobile | APK instalado no celular | URL da API embutida no APK via EAS build |

O App Service NÃO usa `appsettings.Development.json`. Ele usa `appsettings.json` (vazio/placeholder) + environment variables configuradas no Azure Portal.

---

## 3. Configuração de appsettings (Explicação)

### `appsettings.json` (commitado, sem segredos)
- **Usado em**: produção (Azure App Service)
- **Contém**: estrutura/schema dos settings com valores vazios ou placeholder
- **Segredos**: NUNCA — são substituídos por env vars no Azure

### `appsettings.Development.json` (commitado, só dev local)
- **Usado em**: desenvolvimento local (`dotnet run`)
- **Contém**: credenciais do PostgreSQL local (`localhost:5432/couplesync_dev`)
- **Segredos**: apenas do ambiente local (postgres/postgres)

### Como o .NET resolve configuração (ordem de prioridade):
```
1. Environment variables  ← GANHA (produção: Azure Portal)
2. appsettings.{Environment}.json  ← Development no dev, nada em prod
3. appsettings.json  ← Base (placeholders)
```

**Em produção:** env vars do Azure sobrescrevem tudo.  
**Em dev local:** `appsettings.Development.json` sobrescreve `appsettings.json`.

---

## 4. Variáveis de Ambiente no Azure App Service

### Mapeamento Completo

No Azure Portal → App Service → **Configuration** → **Application settings**, configure:

| Nome no Azure | Valor | Para que serve |
|---|---|---|
| `DATABASE_URL` | `Host=ep-xxx.neon.tech;Port=5432;Database=neondb;Username=neondb_owner;Password=xxx;SslMode=Require` | Conexão com Neon PostgreSQL |
| `Jwt__Secret` | String aleatória com 32+ caracteres | Chave de assinatura JWT |
| `Jwt__Issuer` | `CoupleSync` | Issuer do JWT token |
| `Jwt__Audience` | `CoupleSync.Mobile` | Audience do JWT token |
| `Fcm__ProjectId` | `couplesync-78e08` (seu Firebase project ID) | Firebase Cloud Messaging |
| `Fcm__CredentialJson` | Conteúdo completo do `firebase-admin-key.json` | Firebase Admin SDK |
| `Gemini__ApiKey` | `AIzaSy...` (sua key do Google AI Studio) | API do Gemini para AI chat e classificação |
| `Gemini__Enabled` | `true` | Ativar/desativar AI chat |
| `GEMINI_MODEL` | `gemini-2.0-flash` | Modelo do Gemini |
| `USE_LOCAL_PDF_PARSER` | `true` | Usar PdfPig ao invés de Azure Document Intelligence |

### ⚠️ IMPORTANTE: Sintaxe de Variáveis no Azure

O .NET mapeia variáveis de ambiente para seções JSON usando `__` (duplo underscore):

```
appsettings.json:     { "Jwt": { "Secret": "..." } }
Environment variable: Jwt__Secret=minha_chave_secreta
```

Exemplos:
- `Jwt.Secret` → `Jwt__Secret`
- `Fcm.ProjectId` → `Fcm__ProjectId`  
- `Fcm.CredentialJson` → `Fcm__CredentialJson`
- `Gemini.ApiKey` → `Gemini__ApiKey`
- `Gemini.Enabled` → `Gemini__Enabled`

### Para `Fcm__CredentialJson`:
1. Abra `firebase-admin-key.json` em um editor de texto
2. Copie **todo** o conteúdo (de `{` até `}`)
3. Cole no campo **Value** no Azure Portal
4. O Azure aceita JSON multilinha no campo de settings

---

## 5. Banco de Dados — Neon.tech

### Formato da Connection String (Neon)
```
Host=ep-xxx.sa-east-1.aws.neon.tech;Port=5432;Database=neondb;Username=neondb_owner;Password=xxx;SslMode=Require
```

### Como Aplicar Migrations no Neon

As migrations do Entity Framework criam as tabelas. Para aplicá-las no Neon:

```bash
cd backend/src/CoupleSync.Api

# Opção A: Via env var DATABASE_URL (recomendado)
$env:DATABASE_URL = "Host=ep-xxx.neon.tech;Port=5432;Database=neondb;Username=neondb_owner;Password=xxx;SslMode=Require"
dotnet ef database update --project ../CoupleSync.Infrastructure

# Opção B: Via dotnet ef com connection string direta
dotnet ef database update --project ../CoupleSync.Infrastructure -- --connection "Host=ep-xxx.neon.tech;Port=5432;Database=neondb;Username=neondb_owner;Password=xxx;SslMode=Require"
```

### ⚠️ Sobre o Dashboard do Neon

O dashboard do Neon **às vezes não mostra tabelas imediatamente**.

Para confirmar que as tabelas foram criadas:
```bash
# Conectar via psql (instale via scoop: scoop install postgresql)
psql "postgresql://neondb_owner:xxx@ep-xxx.neon.tech/neondb?sslmode=require"

# Verificar tabelas
\dt

# Ou via SQL
SELECT table_name FROM information_schema.tables WHERE table_schema = 'public';
```

Se retornar tabelas como `Users`, `Couples`, `Transactions`, `ImportJobs`, etc., está tudo OK.

O Neon usa o schema `public` por padrão. No dashboard, certifique-se de selecionar:
- **Database:** `neondb` (ou o nome que escolheu)
- **Schema:** `public`
- **Branch:** `main` (default)

---

## 6. Health Check — Como Validar o Backend

### Endpoints disponíveis:

| Endpoint | O que verifica | Autenticação |
|---|---|---|
| `GET /health` | App está viva (status + timestamp) | Nenhuma |
| `GET /health/live` | Liveness probe (app está rodando) | Nenhuma |
| `GET /health/ready` | Readiness probe (inclui check do DB) | Nenhuma |

### Teste no navegador:

```
https://couplesyncapi.azurewebsites.net/health
```

Resposta esperada:
```json
{"status":"healthy","timestamp":"2026-04-18T..."}
```

Se retornar erro:
1. Acesse Azure Portal → App Service → **Log stream** (menu esquerdo)
2. Procure por erros de startup (ex: "Database connection string is missing")
3. Verifique se todas as env vars estão configuradas (seção 4)

### Se `/health` retorna 404:

O App Service pode estar retornando a página padrão do Azure. Isso significa que:
- O deploy não aplicou corretamente, OU
- O app crashou na inicialização

Verifique: Azure Portal → App Service → **Deployment Center** → veja se o último deploy foi sucesso.

---

## 7. CI/CD — GitHub Actions

### Pipelines disponíveis:

| Workflow | Arquivo | Trigger | O que faz |
|---|---|---|---|
| **CI** | `.github/workflows/ci.yml` | Push/PR em `main` | Build + 391 testes + TypeScript check + secret scan |
| **Deploy** | `.github/workflows/deploy.yml` | CI verde em `main` OU manual | Publica no App Service F1 |
| **Mobile APK** | `.github/workflows/mobile-apk.yml` | Tag `v*` OU manual | EAS Build APK + Firebase App Distribution |

### Configurar o Deployment Automático

Para o `deploy.yml` funcionar, você precisa de **1 único secret** no GitHub:

#### Obter o Publish Profile:
1. Azure Portal → App Service `couplesyncapi`
2. Clique **Get publish profile** (botão no topo da Overview)
3. Um arquivo XML é baixado

#### Adicionar como GitHub Secret:
1. GitHub → Settings → Secrets and variables → Actions
2. Clique **New repository secret**
3. Nome: `AZURE_PUBLISH_PROFILE`
4. Valor: Cole **todo o conteúdo XML** do arquivo baixado
5. Clique **Add secret**

Agora, toda vez que você fizer `git push` na `main`:
1. CI roda automaticamente (build + testes)
2. Se CI passa, Deploy roda automaticamente (publica no App Service)

### Configurar o Mobile APK Build (Etapa seguinte)

Para o `mobile-apk.yml` funcionar, adicione estes secrets no GitHub:

| Secret | Como obter |
|---|---|
| `EXPO_TOKEN` | [expo.dev](https://expo.dev/) → Settings → Access Tokens → Create |
| `FIREBASE_TOKEN` | Terminal: `firebase login:ci` → copie o token |
| `FIREBASE_APP_ID` | `1:247947489218:android:8eb748248b90016af4b05b` (do `google-services.json`) |
| `FIREBASE_TESTER_GROUP` | (Opcional) Nome do grupo de testers no Firebase App Distribution |

Para disparar manualmente: GitHub → Actions → "Mobile APK — Build & Distribute" → Run workflow.

Para disparar com tag: `git tag v1.0.0 && git push --tags`

---

## 8. Deploy Manual (Sem CI/CD)

Se preferir fazer deploy manualmente do seu PC:

### Backend (Azure App Service)

```bash
cd backend

# Publicar
dotnet publish src/CoupleSync.Api/CoupleSync.Api.csproj --configuration Release --output ./publish

# Zipar
Compress-Archive -Path ./publish/* -DestinationPath ./deploy.zip -Force

# Deploy via Azure CLI (se tiver az login funcionando)
az webapp deploy --resource-group <SEU_RESOURCE_GROUP> --name couplesyncapi --src-path ./deploy.zip --type zip
```

**OU via Kudu (sem Azure CLI):**
1. Acesse: `https://couplesyncapi.scm.azurewebsites.net/ZipDeployUI`  
2. Faça login com sua conta Azure
3. Arraste o arquivo `deploy.zip` para a página
4. Aguarde o deploy

### Mobile (APK)

```bash
cd mobile

# Login no Expo (primeira vez)
eas login

# Build APK
eas build --platform android --profile preview

# Aguarde ~10 min, copie o link do APK
# Envie para testers via WhatsApp/email ou Firebase App Distribution
```

---

## 9. Checklist Completo de Deploy

### Pré-Deploy
- [ ] Conta Neon.tech criada → connection string copiada
- [ ] Projeto Firebase criado → `google-services.json` em `mobile/`
- [ ] Firebase Admin SDK JSON baixado
- [ ] API key do Gemini gerada em [Google AI Studio](https://aistudio.google.com/app/apikey)
- [ ] App Service F1 criado no Azure Portal

### Configurar
- [ ] **9 env vars configuradas** no Azure Portal (veja seção 4)
- [ ] **Migrations aplicadas** no Neon (`dotnet ef database update` — veja seção 5)
- [ ] **Publish Profile** baixado e salvo como `AZURE_PUBLISH_PROFILE` secret no GitHub

### Deploy Backend
- [ ] Push na `main` → CI verde → Deploy automático
- [ ] OU deploy manual via zip/Kudu
- [ ] Testar: `https://couplesyncapi.azurewebsites.net/health` retorna 200

### Deploy Mobile
- [ ] `eas.json` aponta para `https://couplesyncapi.azurewebsites.net`
- [ ] `eas build --platform android --profile preview` → APK gerado
- [ ] APK instalado no celular → app abre

### Validação Final
- [ ] Registro de usuário funciona
- [ ] Login retorna token
- [ ] Criação de casal funciona
- [ ] Upload de PDF → OCR processa → transações extraídas
- [ ] Dashboard mostra totais corretos

---

## 10. Troubleshooting

| Problema | Causa | Solução |
|---|---|---|
| `/health` retorna 404 | Deploy não aplicou ou app crashou | Azure Portal → Log stream → ver erro |
| App Service → 500 | Env var faltando | Verifique todas as 9 env vars (seção 4) |
| "Invalid JWT secret" no log | `Jwt__Secret` vazio ou < 32 chars | Configure `Jwt__Secret` com 32+ chars |
| "Database connection string is missing" | `DATABASE_URL` não configurado | Adicione env var `DATABASE_URL` |
| Neon dashboard não mostra tabelas | Schema/branch errado no UI | Use `psql` + `\dt` para confirmar |
| APK não conecta na API | URL errada no APK | Verifique `EXPO_PUBLIC_API_BASE_URL` em `eas.json` |
| FCM não envia push | `Fcm__CredentialJson` truncado/errado | Recopie todo o JSON do firebase-admin-key.json |
| Gemini retorna erro | API key inválida | Verifique `Gemini__ApiKey` |
| CI falha no GitHub | Testes falhando | Corrija os testes localmente, push novamente |

---

## 11. Segurança — O Que Nunca Commitar

**Já está no `.gitignore`:**
- `mobile/google-services.json`
- `backend/src/CoupleSync.Api/uploads/`

**Você deve garantir que NUNCA estejam no Git:**
- Connection strings reais do Neon
- JWT secrets de produção  
- Firebase Admin SDK JSON
- Gemini API keys
- Azure Publish Profile XML

Se já commitou algum desses por engano:
1. Gere novos valores (nova password no Neon, nova API key, etc.)
2. Atualize no Azure Portal
3. O valor antigo commitado está comprometido — considere-o inválido

---

## 12. Resumo Visual

```
┌── DESENVOLVIMENTO ───────────────────────────────────┐
│                                                       │
│  dotnet run  ←── appsettings.Development.json         │
│  localhost:5000   (PostgreSQL local, JWT dev)          │
│                                                       │
│  expo start  ←── .env (API_BASE_URL=localhost:5000)   │
│                                                       │
└───────────────────────────────────────────────────────┘

       git push main
            │
            ▼

┌── CI (GitHub Actions) ───────────────────────────────┐
│  Build → 391 testes → TypeScript check → Secret scan │
└──────────────────────┬───────────────────────────────┘
                       │ se verde
                       ▼

┌── DEPLOY (GitHub Actions) ───────────────────────────┐
│  dotnet publish → upload para App Service F1         │
└──────────────────────┬───────────────────────────────┘
                       │
                       ▼

┌── PRODUÇÃO ──────────────────────────────────────────┐
│                                                       │
│  App Service F1  ←── Environment Variables            │
│  couplesyncapi.azurewebsites.net                      │
│      │                                                │
│      ├── DATABASE_URL → Neon PostgreSQL               │
│      ├── Jwt__Secret → JWT signing                    │
│      ├── Fcm__* → Firebase push notifications         │
│      └── Gemini__* → AI chat + OCR categorization     │
│                                                       │
└───────────────────────────────────────────────────────┘

       eas build --profile preview
            │
            ▼

┌── MOBILE APK ────────────────────────────────────────┐
│  EAS Cloud Build → APK com URL de produção embutida  │
│  → Firebase App Distribution → testadores instalam   │
└───────────────────────────────────────────────────────┘
```
