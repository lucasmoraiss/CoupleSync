---
description: "Use when writing or modifying C# backend code — controllers, services, entities, DTOs, validators, migrations, or tests in backend/src/ or backend/tests/."
applyTo: "backend/**/*.cs"
---
# .NET Backend Conventions (CoupleSync)

## Architecture (Clean Architecture)
- **Domain** (`Domain/Entities/`): Plain C# classes, no framework dependencies. Business rules live here.
- **Application** (`Application/`): Feature slices mirroring controller names. Contains services, DTOs (`record` types), interfaces.
- **Infrastructure** (`Infrastructure/`): EF Core DbContext, repository implementations, external integrations, background jobs.
- **Api** (`Api/Controllers/`): Thin controllers — validate input, delegate to Application services, return results.

## Patterns
- DTOs are `record` types; entities are `class` types
- Every query MUST filter by `CoupleId` — couple-level data isolation is mandatory
- Input validation: FluentValidation in `Api/Validators/`
- JWT auth with refresh tokens via `Infrastructure/Security/`
- Use `IDateTimeProvider` (not `DateTime.UtcNow`) for testability

## Naming
- Controllers: `{Feature}Controller.cs` (e.g., `TransactionsController.cs`)
- Services: `{Feature}Service.cs` in `Application/{Feature}/`
- Entities: singular PascalCase (e.g., `Transaction`, `BudgetPlan`)
- Migrations: auto-generated names from `dotnet ef migrations add`

## Testing
- Unit tests: `backend/tests/CoupleSync.UnitTests/` — test Application services
- Integration tests: `backend/tests/CoupleSync.IntegrationTests/` — test with real DB
- E2E tests: `backend/tests/CoupleSync.E2ETests/` — test full HTTP pipeline
- Test naming: `MethodName_Scenario_ExpectedResult`
