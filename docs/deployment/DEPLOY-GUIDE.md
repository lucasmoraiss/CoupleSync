# CoupleSync — Guia de implantação

> **Situação em 05/out/2026.** A implantação antiga da API no **Azure App Service está desativada** e fica registrada na [seção 9](#9-histórico-azure-app-service-desativado) apenas como histórico. A implantação vigente é **Render (serviço web, plano gratuito) + Neon (PostgreSQL)**. Ainda não há URL pública divulgada; este guia usa o marcador `https://<seu-servico>.onrender.com`.
>
> Este guia não contém identificadores reais de infraestrutura. Onde aparecer `<...>`, substitua pelo valor do seu ambiente.

---

## 1. Visão geral

```
┌──────────────────┐     HTTPS      ┌─────────────────────────────────────┐
│  App Android     │ ─────────────► │  Render — serviço web (Docker)      │
│  (Expo / EAS)    │                │  https://<seu-servico>.onrender.com │
└──────────────────┘                │                                     │
                                    │  API .NET 8                         │
                                    │  ├─ parser local de PDF (PdfPig)    │
                                    │  ├─ jobs em segundo plano           │
                                    │  ├─ push via FCM (opcional)         │
                                    │  └─ assistente via Gemini (opcional)│
                                    └──────────────────┬──────────────────┘
                                                       │
                        ┌──────────────────────────────┼─────────────────────────┐
                        ▼                              ▼                         ▼
                ┌───────────────┐            ┌──────────────────┐      ┌──────────────────┐
                │ Neon          │            │ Firebase (FCM)   │      │ Google Gemini    │
                │ PostgreSQL    │            │ opcional         │      │ opcional         │
                └───────────────┘            └──────────────────┘      └──────────────────┘
```

A API é um único contêiner construído a partir de `backend/Dockerfile`. Os jobs de importação de extrato e de envio de push rodam dentro do mesmo processo ([ADR-0003](../adr/0003-jobs-em-processo.md)).

---

## 2. Banco de dados no Neon

1. Crie um projeto no Neon e um banco (por exemplo, `couplesync`).
2. No painel, copie os dados de conexão: host, banco, usuário e senha.
3. Monte a `DATABASE_URL` no formato de pares:

```
Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require
```

A API também aceita o formato URI (`postgresql://<usuario>:<senha>@<host-do-neon>/<banco>?sslmode=require`). Quando o host é do Neon, o código aplica sozinho `SslMode=Require` e os limites de pool (`MaxPoolSize=10`, `MinPoolSize=1`, `ConnectionIdleLifetime=240`); veja `backend/src/CoupleSync.Infrastructure/Persistence/DatabaseConnectionResolver.cs`.

**Migrações.** A API aplica as migrações pendentes ao iniciar (`Program.cs`, `MigrateAsync`) e carrega as regras de categorização. Não há passo manual de migração na implantação. Hoje são 13 migrações.

Para aplicar à mão, a partir da sua máquina (opcional):

```powershell
cd backend
$env:DATABASE_URL = "Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require"
dotnet ef database update --project src/CoupleSync.Infrastructure --startup-project src/CoupleSync.Api
```

Detalhes do Neon em [docs/architecture/neon-setup-guide.md](../architecture/neon-setup-guide.md).

---

## 3. Implantação no Render

### 3.1 Criar o serviço

**Pelo blueprint (recomendado).** A raiz do repositório tem um `render.yaml` que descreve o serviço. No Render, crie um **Blueprint** apontando para o repositório; o serviço web é criado com as configurações e variáveis abaixo já preenchidas. Ficam para você informar no painel:

- `DATABASE_URL` (a conexão do Neon);
- `GEMINI_API_KEY`, só se for ligar a IA;
- as variáveis `Fcm__*`, só se for usar push (não estão no blueprint).

O blueprint pede ao Render que gere o valor de `JWT__SECRET`.

**Manualmente.** Crie um **Web Service** ligado ao repositório do GitHub e configure:

| Campo | Valor |
|---|---|
| Ambiente de execução | Docker |
| Diretório raiz (Root Directory) | `backend` |
| Dockerfile | `./Dockerfile` (ou seja, `backend/Dockerfile`) |
| Plano | Gratuito (Free) |
| Health Check Path | `/health/live` |

O `Dockerfile` compila a API em uma imagem Alpine, roda com usuário sem privilégios e cria a pasta `/app/uploads` com permissão de escrita.

**Por que `/health/live` e não `/health/ready`.** `/health/live` só confirma que o processo está de pé. `/health/ready` consulta o banco; usá-lo como verificação periódica geraria consultas constantes ao banco e faria o serviço ser dado como indisponível a cada falha passageira do banco.

### 3.2 Porta

O `Dockerfile` fixa `ASPNETCORE_URLS=http://+:8080` e expõe a porta 8080. A API **não lê** a variável `PORT`. Defina `PORT=8080` no Render para que a plataforma encaminhe o tráfego para a porta em que o contêiner escuta.

### 3.3 Variáveis de ambiente

Configure em **Environment** no serviço do Render. Marque como secretas as que guardam credenciais.

**Obrigatórias**

| Variável | Valor | Observação |
|---|---|---|
| `DATABASE_URL` | `Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require` | Conexão com o Neon. |
| `JWT__SECRET` | chave gerada, com 32 caracteres ou mais | A API não inicia sem ela. Gere com `openssl rand -hex 32`. Trocar a chave invalida todas as sessões. |
| `JWT__ISSUER` | `CoupleSync` | Padrão do código; declare para deixar explícito. |
| `JWT__AUDIENCE` | `CoupleSync.Mobile` | Idem. |
| `USE_LOCAL_PDF_PARSER` | `true` | Usa o parser local de PDF. É o padrão do código. |
| `Storage__BasePath` | `/app/uploads` | Pasta temporária dos PDFs enviados (criada pelo `Dockerfile`). |
| `ForwardedHeaders__TrustAllProxies` | `true` | Veja a explicação abaixo. |
| `ForwardedHeaders__ClientIpHeader` | `CF-Connecting-IP` | Veja a explicação abaixo. |
| `PORT` | `8080` | Veja 3.2. |

Todas as variáveis desta tabela já estão declaradas no `render.yaml`; `DATABASE_URL` vem sem valor e `JWT__SECRET` é gerada pelo Render.

**Opcionais**

| Variável | Valor | Para que serve |
|---|---|---|
| `JWT__ACCESSTOKENTTLMINUTES` | padrão `15` | Validade do access token. |
| `JWT__REFRESHTOKENTTLDAYS` | padrão `7` | Validade do refresh token. |
| `RateLimiting__Auth__PermitLimit`, `RateLimiting__Auth__WindowSeconds` | padrão `5` e `60` | Limite de login e cadastro por IP. |
| `RateLimiting__CoupleJoin__PermitLimit`, `RateLimiting__CoupleJoin__WindowSeconds` | padrão `5` e `60` | Limite de entrada em grupo por usuário. |
| `AI_CHAT_ENABLED` | `true` | Liga o assistente e a categorização automática dos candidatos do extrato. |
| `GEMINI_API_KEY` | chave do Google AI Studio | Necessária com a IA ligada. |
| `GEMINI_MODEL` | padrão `gemini-2.0-flash` | Modelo do Gemini. |
| `Fcm__ProjectId` | `<id-do-projeto-firebase>` | Envio de push. |
| `Fcm__CredentialJson` | conteúdo do JSON da conta de serviço do Firebase | Envio de push. Sem as duas variáveis `Fcm__*`, os alertas não são enviados e o log registra `FCM is not configured`. |

**Por que `ForwardedHeaders__TrustAllProxies=true`.** O limite de tentativas de login e cadastro (5 por minuto) é contado por IP do cliente. No Render, toda requisição chega ao contêiner pelo proxy da plataforma. Por padrão, o ASP.NET Core só aceita o cabeçalho `X-Forwarded-For` vindo do próprio `localhost`; sem essa variável, a API enxergaria o IP do proxy e **todos os usuários dividiriam o mesmo contador**, de modo que cinco logins quaisquer em um minuto bloqueariam os demais. Como o endereço do proxy do Render não é fixo, não dá para listá-lo em `KnownProxies`; `TrustAllProxies=true` faz a API aceitar o cabeçalho de qualquer origem. Isso é seguro apenas porque o contêiner não é alcançável por fora do proxy. Em um ambiente onde a API fique exposta diretamente, use `ForwardedHeaders__KnownProxies__0` ou `ForwardedHeaders__KnownNetworks__0`. A lógica está em `backend/src/CoupleSync.Api/RateLimiting/RateLimitingSetup.cs`.

**Por que `ForwardedHeaders__ClientIpHeader=CF-Connecting-IP`.** O Render fica atrás do Cloudflare, e o proxy acrescenta endereços ao `X-Forwarded-For`: o último deles é a borda do Cloudflare, que muda a cada requisição. Só com `TrustAllProxies`, cada tentativa de login cairia em um contador diferente e o limite nunca dispararia (foi o que aconteceu na primeira publicação). O Cloudflare envia o endereço real do cliente no cabeçalho `CF-Connecting-IP`, e é por ele que a API passa a contar. Para conferir: seis logins seguidos com senha errada devem terminar em `429`.

### 3.4 Comportamento do plano gratuito

- **Suspensão.** O serviço é suspenso após cerca de 15 minutos sem tráfego e leva cerca de 1 minuto para acordar na requisição seguinte. O app desiste de uma requisição após 30 segundos, então a primeira tentativa depois de um período parado pode falhar; a seguinte funciona.
- **Jobs em segundo plano param junto.** A importação de extrato e o envio de push rodam dentro do processo da API. Com o serviço suspenso, nada é processado até a próxima requisição acordá-lo.
- **Disco efêmero.** Arquivos gravados no contêiner somem a cada deploy ou reinício. Para o CoupleSync isso não causa perda de dados: o PDF enviado fica em disco só até o job processá-lo e é apagado em seguida, tanto no sucesso quanto na falha definitiva (`OcrBackgroundJob`, chamadas a `TryDeleteFileAsync`). Tudo o que precisa durar está no PostgreSQL. O único efeito possível é uma importação que estava na fila no momento do reinício falhar por não encontrar o arquivo; basta enviar o PDF de novo.
- **Limites de tentativas e do assistente ficam em memória.** Os contadores zeram quando o serviço reinicia.

### 3.5 Verificar

```bash
curl https://<seu-servico>.onrender.com/health/live    # processo de pé
curl https://<seu-servico>.onrender.com/health/ready   # banco respondendo
```

`/health/ready` devolve 200 quando a API consegue consultar o banco. Em seguida, teste um cadastro:

```bash
curl -X POST https://<seu-servico>.onrender.com/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"teste@example.com","name":"Teste","password":"<senha-de-8-ou-mais>"}'
```

### 3.6 Atualizações

O deploy é feito pelo próprio Render, que reconstrói a imagem a partir do repositório. O `render.yaml` liga o deploy automático (`autoDeploy: true`), que dispara a cada push no branch configurado no serviço; também é possível disparar pelo painel. Não há workflow de deploy do back-end para o Render no GitHub Actions.

---

## 4. Ambiente de desenvolvimento

```powershell
# Terminal 1 — API em http://localhost:5000
$env:JWT__SECRET = "<chave-de-32-ou-mais-caracteres>"
$env:DATABASE_URL = "Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=<sua-senha>"
dotnet run --project backend/src/CoupleSync.Api --launch-profile http

# Terminal 2 — app
cd mobile
npx expo start --android   # usa mobile/.env → EXPO_PUBLIC_API_BASE_URL
```

Passo a passo completo em [docs/dev-guide.md](../dev-guide.md).

### Como a configuração é resolvida

```
1. Variáveis de ambiente                 ← prevalecem
2. appsettings.{Ambiente}.json           ← só existe na máquina de quem desenvolve (ignorado pelo Git)
3. appsettings.json                      ← versionado; Jwt:Secret e a conexão vêm vazios
```

`DATABASE_URL` é lida diretamente do ambiente e tem precedência sobre `ConnectionStrings:DefaultConnection`. Variáveis com `__` representam seções: `JWT__SECRET` equivale a `Jwt:Secret`, `Fcm__ProjectId` a `Fcm:ProjectId`.

---

## 5. App Android (APK)

O endereço da API é embutido no app no momento do build, pela variável `EXPO_PUBLIC_API_BASE_URL` (raiz da API, sem `/api/v1`).

1. Em `mobile/eas.json`, ajuste `EXPO_PUBLIC_API_BASE_URL` dos perfis `preview` e `production` para `https://<seu-servico>.onrender.com`.
2. Gere o APK:

```bash
cd mobile
eas login
eas build --platform android --profile preview
```

3. Baixe o APK pelo link devolvido pelo EAS e instale no aparelho.

APKs gerados antes da troca de hospedagem continuam apontando para o endereço antigo e precisam ser substituídos por um build novo (ou receber uma atualização OTA com a URL nova).

Mais opções em [docs/architecture/apk-generation-guide.md](../architecture/apk-generation-guide.md).

---

## 6. GitHub Actions

| Workflow | Arquivo | O que faz |
|---|---|---|
| CI | `.github/workflows/ci.yml` | Em push e pull request para `main`: build do back-end, migrações em um PostgreSQL 16 de serviço, testes de unidade, integração e ponta a ponta (588 no total), `expo-doctor` (informativo), checagem de tipos do app (`tsc --noEmit`), varredura de segredos e validação do `docker build`. |
| APK | `.github/workflows/mobile-apk.yml` | Gera o APK pelo EAS (perfil `production`) e o publica como artefato do workflow. Pode ser disparado manualmente ou por tag `v*`. |
| Atualização OTA | `.github/workflows/mobile-update.yml` | Publica uma atualização JavaScript pelo EAS Update no branch `production`. Pode ser disparado manualmente. |

O CI não executa os testes Jest do app (`npm test`); rode-os localmente.

Segredos usados pelos workflows do app:

| Segredo | Usado por | Observação |
|---|---|---|
| `EXPO_TOKEN` | APK e atualização OTA | Token de acesso da conta Expo. |
| `EXPO_PUBLIC_API_BASE_URL` | atualização OTA | URL da API embutida no pacote JavaScript. |

---

## 7. Problemas comuns

| Sintoma | Causa provável | O que fazer |
|---|---|---|
| O serviço não inicia e o log mostra `Invalid JWT secret configuration` | `JWT__SECRET` ausente ou com menos de 32 caracteres | Gere uma chave e defina a variável. |
| Log `Database connection string is missing` | `DATABASE_URL` ausente | Defina a variável. |
| `/health/live` responde, `/health/ready` não | Banco inacessível ou credenciais erradas | Revise a `DATABASE_URL`. |
| O Render não detecta o serviço no ar | Porta divergente | Defina `PORT=8080`. |
| Primeira requisição demora ou expira | Serviço suspenso no plano gratuito | Repita após cerca de um minuto. |
| Vários usuários recebem 429 no login ao mesmo tempo | API contando o IP do proxy | Defina `ForwardedHeaders__TrustAllProxies=true`. |
| O limite de tentativas nunca dispara (o 6º login errado recebe 401) | API contando um endereço de proxy que muda a cada requisição | Defina `ForwardedHeaders__ClientIpHeader=CF-Connecting-IP`. |
| Não sei qual versão está publicada | — | `GET /health` devolve o commit em `version`. |
| O app não conecta | URL errada no build | Confira `EXPO_PUBLIC_API_BASE_URL` em `mobile/eas.json` e gere o APK de novo. |
| Alertas não chegam por push | `Fcm__*` ausentes ou inválidas | Revise as credenciais do Firebase. |
| O assistente responde 404 `AI_CHAT_DISABLED` | IA desligada | Defina `AI_CHAT_ENABLED=true` e `GEMINI_API_KEY`. |

---

## 8. O que nunca vai para o Git

Ignorados pelo `.gitignore`:

- `.env` e `.env.*` (exceto `.env.example`)
- `**/appsettings.Development.json` e demais `appsettings.*.json`
- `mobile/google-services.json`
- `backend/publish/`, `backend/deploy.zip` e `**/publish-profile.xml`
- `backend/src/CoupleSync.Api/uploads/`

Também não devem ser versionados: a `DATABASE_URL` real, a chave JWT, o JSON da conta de serviço do Firebase e a chave do Gemini. Se algum desses valores for commitado por engano, considere-o comprometido: gere um novo e atualize o ambiente.

---

## 9. Histórico: Azure App Service (desativado)

Entre abril de 2026 e a migração para o Render, a API rodou em um **Azure App Service para Linux, no plano gratuito F1**, publicada por zip (`dotnet publish` + publish profile) e iniciada com `dotnet CoupleSync.Api.dll`. O banco já era o Neon. Os PDFs enviados ficavam no disco local do App Service até o processamento.

Essa implantação foi **desativada**. Nomes de recurso, grupo de recursos e plano foram retirados deste guia; se precisar recriar algo parecido, use `<nome-do-app-service>` e `<grupo-de-recursos>` no lugar. O workflow do GitHub Actions que publicava no App Service (`deploy.yml`) foi removido do repositório.

A escolha original de nuvem e a análise que a embasou estão em [ADR-0005](../adr/0005-stack-de-nuvem.md) e em [docs/architecture/cloud-deployment-analysis.md](../architecture/cloud-deployment-analysis.md). O ADR previa Azure Container Apps; o que entrou em operação foi o App Service.
