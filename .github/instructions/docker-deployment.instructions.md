---
description: "Use when working with Docker, docker-compose, container configuration, or deployment to Azure Container Apps."
applyTo: ["docker-compose.yml", "backend/Dockerfile", "backend/publish/**"]
---
# Docker & Deployment Conventions (CoupleSync)

## Local Development
```bash
docker compose up -d          # Start PostgreSQL + API
docker compose down           # Stop all
docker compose logs -f api    # Tail API logs
```

## Dockerfile
- Located at `backend/Dockerfile`
- Multi-stage build: restore → build → publish → runtime
- Base image: `mcr.microsoft.com/dotnet/aspnet:8.0`

## Environment Variables
- `DATABASE_URL` — PostgreSQL connection string
- `JWT__SECRET`, `JWT__ISSUER`, `JWT__AUDIENCE` — JWT config
- `POSTGRES_PASSWORD` — local dev only
- See `.env.example` for full list

## Azure Container Apps
- Deployed to Azure Container Apps (East US)
- Production URL pattern: `couplesync-api.eastus.azurecontainerapps.io`
- Publish profile: `backend/publish-profile.xml` (gitignored in production)
