# Neon.tech PostgreSQL Setup Guide

This guide explains how to provision a free Neon.tech database for CoupleSync and configure the connection string in local development, CI, and Azure Container Apps.

---

## 1. Create a Free Neon Project

1. Go to [https://neon.tech](https://neon.tech) and sign in (GitHub login recommended).
2. Click **New Project**.
3. Choose a project name (e.g. `couplesync-pilot`), region closest to your deployment (e.g. `AWS us-east-1`), and PostgreSQL version **16**.
4. Click **Create Project** — this provisions the serverless endpoint in under a minute.
5. From the **Connection Details** panel, select your branch (`main`) and copy the **Connection string** in Npgsql/ADO.NET format.

Free-tier limits:
| Resource | Limit |
|---|---|
| Storage | 10 GB |
| Branches | 10 |
| Compute (connection) | ~20 simultaneous connections |
| Idle auto-suspend | 5 minutes of inactivity; wakes in ~500 ms |

---

## 2. Get the Connection String

In the Neon console:

1. Open your project → **Connection Details** → select **Connection string** → choose **Npgsql (ADO.NET)**.
2. The string will look like:

```
Host=ep-cool-name-123456.us-east-2.aws.neon.tech;Port=5432;Database=couplesync;Username=couplesync_owner;Password=<password>;SslMode=Require
```

> **Note**: Do NOT add `TrustServerCertificate=true` — Neon uses a valid CA-signed certificate and the backend resolver applies the correct SSL and pool settings automatically for `*.neon.tech` hosts.

```
MaxPoolSize=10;MinPoolSize=1;ConnectionIdleLifetime=240
```

You do **not** need to add these manually — they are injected at runtime.

---

## 3. Configure the Connection String

### Local development (`.env` or user secrets)

**Option A — Environment variable (recommended)**

Set `DATABASE_URL` before running the API:

```bash
# PowerShell
$env:DATABASE_URL = "Host=ep-cool-name-123456.us-east-2.aws.neon.tech;Port=5432;Database=couplesync;Username=couplesync_owner;Password=<password>;SslMode=Require"
dotnet run --project backend/src/CoupleSync.Api
```

```bash
# Bash / macOS
export DATABASE_URL="Host=ep-cool-name-123456.us-east-2.aws.neon.tech;Port=5432;Database=couplesync;Username=couplesync_owner;Password=<password>;SslMode=Require"
dotnet run --project backend/src/CoupleSync.Api
```

**Option B — .NET user secrets (never committed)**

```bash
cd backend/src/CoupleSync.Api
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=ep-cool-name-123456.us-east-2.aws.neon.tech;Port=5432;Database=couplesync;Username=couplesync_owner;Password=<password>;SslMode=Require"
```

### Run migrations against Neon

```bash
DATABASE_URL="<neon-connection-string>" dotnet ef database update \
  --project backend/src/CoupleSync.Infrastructure \
  --startup-project backend/src/CoupleSync.Api
```

---

## 4. Configure in Azure Container Apps

Set `DATABASE_URL` as a **secret** in your Container App, then mount it as an environment variable. Never put the real connection string in source files.

### Using the Azure CLI

```bash
# Create the secret
az containerapp secret set \
  --name couplesync-api \
  --resource-group rg-couplesync-pilot \
  --secrets "database-url=<neon-connection-string>"

# Mount the secret as an environment variable
az containerapp update \
  --name couplesync-api \
  --resource-group rg-couplesync-pilot \
  --set-env-vars "DATABASE_URL=secretref:database-url"
```

### Using GitHub Actions (for `deploy.yml`)

Add the connection string to your repository's **GitHub Secrets** as `NEON_DATABASE_URL`, then reference it in the workflow:

```yaml
- name: Set DATABASE_URL secret in ACA
  run: |
    az containerapp secret set \
      --name couplesync-api \
      --resource-group ${{ vars.AZURE_RESOURCE_GROUP }} \
      --secrets "database-url=${{ secrets.NEON_DATABASE_URL }}"
```

---

## 5. Connection String Reference

| Parameter | Value | Reason |
|---|---|---|
| `Host` | `ep-xxx.region.neon.tech` | Neon serverless endpoint |
| `Port` | `5432` | Standard PostgreSQL |
| `SslMode` | `Require` | Neon requires TLS |
| `TrustServerCertificate` | *not set* | Neon uses a valid CA-signed cert; full validation is the secure default |
| `MaxPoolSize` | `10` | Stay within Neon free-tier ~20 connection cap |
| `MinPoolSize` | `1` | Keep one connection warm to reduce cold-wake latency |
| `ConnectionIdleLifetime` | `240` | Return connections to pool before Neon's 5-min idle-suspend (4-min buffer) |

> **Security note**: Never hardcode credentials in source files. Always use environment variables or secrets management. The `PASSWORD` field in appsettings files must always be a placeholder.
