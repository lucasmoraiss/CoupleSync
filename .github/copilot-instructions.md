# CoupleSync — Project Instructions for AI Agents

## Product Context
CoupleSync is an Android-first budgeting app for couples. Pilot scope: up to 10 users (5 couples). Prioritize usability, trust, and iteration speed over horizontal scalability.

## Stack
| Layer | Technology | Key paths |
|-------|-----------|-----------|
| Mobile | React Native + Expo (SDK 52) | `mobile/` — Expo Router in `app/`, features in `src/modules/` |
| Backend | .NET 8 Web API, Clean Architecture | `backend/src/` — Api, Application, Domain, Infrastructure |
| Database | PostgreSQL 16 | Migrations: `backend/src/CoupleSync.Infrastructure/Persistence/Migrations/` |
| Notifications | Firebase Cloud Messaging | Config: `mobile/google-services.json` (gitignored) |
| CI/CD | GitHub Actions | `.github/workflows/ci.yml` |

## Build & Test Commands
```bash
# Backend
dotnet restore backend/CoupleSync.sln
dotnet build backend/CoupleSync.sln --configuration Release
dotnet test backend/tests/CoupleSync.UnitTests/ --no-build --configuration Release
dotnet test backend/tests/CoupleSync.IntegrationTests/ --no-build --configuration Release
dotnet test backend/tests/CoupleSync.E2ETests/ --no-build --configuration Release

# Mobile
cd mobile && npm ci && npx tsc --noEmit

# Docker (local full stack)
docker compose up -d
```

## Architecture Conventions

### Backend (.NET)
- **Clean Architecture layers**: Domain → Application → Infrastructure → Api
- Controllers in `Api/Controllers/` — thin, delegate to Application services
- Feature slices in `Application/` mirror controller names (Auth, Budget, CashFlow, Goals, Transactions, etc.)
- Domain entities in `Domain/Entities/` — 14 entities including User, Couple, Transaction, BudgetPlan, Goal
- EF Core with PostgreSQL; migrations via `dotnet ef`
- Couple-level data isolation enforced on every query via `CoupleId` filter
- JWT authentication with refresh tokens
- Use `record` types for DTOs; `class` for entities

### Mobile (React Native / Expo)
- Expo Router file-based routing in `mobile/app/`
- Feature modules in `mobile/src/modules/` (chat, integrations, ocr, transactions)
- Shared components in `mobile/src/components/`
- State management: Zustand stores in `mobile/src/state/`
- API client: `mobile/src/services/apiClient.ts`
- Theme tokens: `mobile/src/theme/`

## Security and Privacy Rules
- Never store raw banking credentials
- Couple-level data isolation is mandatory for every endpoint and query
- Secrets must come from environment configuration, never hardcoded
- Validate all external provider payloads before persistence
- Input validation at API boundary using FluentValidation

## UX Rules
- Android-first interactions and ergonomics
- Core actions reachable in ≤2 taps
- Financial language: clear, neutral, non-judgmental
- Surface integration failures with explicit status and recovery guidance

## MCP Servers Available
Agents that need CI/CD information should use the `github-actions` MCP server tools:
- `list_workflows` — enumerate repository workflows
- `list_workflow_runs` — check recent CI run status and results
- `get_workflow_run` — detailed info for a specific run
- `list_workflow_jobs` / `get_job_logs` — drill into job failures and read logs
- `trigger_workflow` — dispatch a workflow_dispatch event
- `rerun_workflow` — re-run failed jobs
- `get_workflow_file` / `validate_workflow_file` — read and validate workflow YAML
- `list_run_artifacts` — list build artifacts

**When to use**: Checking CI status after code changes, debugging failing pipelines, triggering deployments, validating workflow configuration changes.

## Artifact Discipline
- Keep `.agents-work/<session>/` artifacts (spec.md, acceptance.json, tasks.yaml, status.json) consistent
- Update status.json `last_update` whenever a state change is made
- `tasks.yaml` is the single source of truth for task status

## Scope Guardrails
- V1 excludes iOS, microservices decomposition, and advanced AI-driven categorization
- Do not expand scope without explicit user decision and artifact updates
