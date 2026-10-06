# Banco de dados no Neon — guia de configuração

Como criar um banco PostgreSQL no Neon para o CoupleSync e informar a conexão à API em desenvolvimento, no CI e na hospedagem (Render).

Este guia usa marcadores (`<host-do-neon>`, `<banco>`, `<usuario>`, `<senha>`) no lugar de valores reais. Nunca versione a conexão verdadeira.

---

## 1. Criar o projeto

1. Acesse o painel do Neon e entre com a sua conta.
2. Crie um projeto, escolhendo a região mais próxima de onde a API roda.
3. Crie (ou use) um banco, por exemplo `couplesync`.
4. Em **Connection Details**, copie host, banco, usuário e senha.

O Neon suspende o banco depois de um período sem conexões e o reativa na conexão seguinte, o que deixa a primeira consulta mais lenta. Consulte o painel do Neon para os limites vigentes do plano gratuito.

---

## 2. Formato da conexão

A API lê a variável de ambiente `DATABASE_URL`. O formato recomendado é o de pares:

```
Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require
```

Também é aceito o formato URI:

```
postgresql://<usuario>:<senha>@<host-do-neon>/<banco>?sslmode=require
```

Quando o host contém `neon.tech`, o código (`DatabaseConnectionResolver`) aplica automaticamente:

| Parâmetro | Valor | Motivo |
|---|---|---|
| `SslMode` | `Require` | O Neon exige TLS. |
| `MaxPoolSize` | `10` | Manter folga em relação ao limite de conexões do plano gratuito. |
| `MinPoolSize` | `1` | Manter uma conexão pronta. |
| `ConnectionIdleLifetime` | `240` | Devolver conexões ociosas antes de o Neon suspender o banco. |

Não é preciso acrescentar esses parâmetros à mão. Não adicione `TrustServerCertificate=true`: o certificado do Neon é validado normalmente.

---

## 3. Onde configurar

### Desenvolvimento local

```powershell
# PowerShell
$env:DATABASE_URL = "Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require"
$env:JWT__SECRET = "<chave-de-32-ou-mais-caracteres>"
dotnet run --project backend/src/CoupleSync.Api --launch-profile http
```

```bash
# Bash
export DATABASE_URL="Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require"
export JWT__SECRET="<chave-de-32-ou-mais-caracteres>"
dotnet run --project backend/src/CoupleSync.Api --launch-profile http
```

Como alternativa, coloque a conexão em `ConnectionStrings:DefaultConnection` no arquivo `backend/src/CoupleSync.Api/appsettings.Development.json`, que é ignorado pelo Git. `DATABASE_URL`, quando definida, tem precedência.

### Render

No serviço web, em **Environment**, crie a variável `DATABASE_URL` com a conexão e marque-a como secreta. As demais variáveis estão em [docs/deployment/DEPLOY-GUIDE.md](../deployment/DEPLOY-GUIDE.md#33-variáveis-de-ambiente).

### CI

O workflow `ci.yml` não usa o Neon: ele sobe um PostgreSQL 16 como serviço do próprio job e aponta `DATABASE_URL` para ele.

---

## 4. Migrações

A API aplica as migrações pendentes ao iniciar. Depois de configurar `DATABASE_URL` e subir a API, o esquema já está criado; confira com:

```bash
curl https://<seu-servico>.onrender.com/health/ready
```

Para aplicar à mão, a partir da sua máquina:

```bash
DATABASE_URL="Host=<host-do-neon>;Port=5432;Database=<banco>;Username=<usuario>;Password=<senha>;SslMode=Require" \
  dotnet ef database update \
  --project backend/src/CoupleSync.Infrastructure \
  --startup-project backend/src/CoupleSync.Api
```

---

## 5. Segurança

- A conexão real fica só em variáveis de ambiente ou em arquivos ignorados pelo Git.
- O `appsettings.json` versionado traz `ConnectionStrings:DefaultConnection` vazio.
- Se a conexão vazar, troque a senha do usuário no painel do Neon e atualize a variável na hospedagem.
