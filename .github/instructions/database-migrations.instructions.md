---
description: "Use when creating or modifying EF Core database migrations, schema changes, DbContext configuration, or data model changes."
applyTo: "backend/src/CoupleSync.Infrastructure/Persistence/**"
---
# Database & Migration Conventions (CoupleSync)

## Technology
- PostgreSQL 16, EF Core with Npgsql provider
- DbContext: `CoupleSync.Infrastructure.Persistence.AppDbContext`
- Migrations folder: `Persistence/Migrations/`

## Creating Migrations
```bash
cd backend
dotnet ef migrations add <MigrationName> \
  -p src/CoupleSync.Infrastructure/CoupleSync.Infrastructure.csproj \
  -s src/CoupleSync.Api/CoupleSync.Api.csproj
```

## Rules
- Always create reversible migrations — implement both `Up()` and `Down()`
- Never drop columns in the same release as code removal
- Every table with user data MUST have a `CoupleId` foreign key for data isolation
- Test migrations against a clean database before committing
- Use `HasIndex` for frequently queried columns (e.g., `CoupleId`, `Date`)
