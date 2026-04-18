# CoupleSync V1.5 Architecture

> This document extends the V1 architecture (`.agents-work/2026-04-13_couplesync-ai-driven-bootstrap/architecture.md`).
> All V1 design decisions (modular monolith, couple-level isolation, notification capture pipeline, FCM alerts) remain unchanged.
> V1.5 adds: cloud deployment to Azure/Neon, budget management, OCR import, UI polish tokens, and AI financial chat.

---

## Overview

CoupleSync V1.5 migrates the existing .NET 8 modular-monolith API from a local server to Azure Container Apps (Consumption Plan) backed by Neon.tech serverless PostgreSQL, keeping every service on free tier for the pilot cohort. Three new product modules are added on top of the V1 foundation: **Budget** (monthly income and per-category allocation with overspend FCM alerts), **OCR Import** (camera/PDF upload → Azure AI Document Intelligence extraction → user-review merge), and **AI Chat** (Gemini Flash 2.0 financial assistant, last priority). A unified mobile theme system and shared UI-state components are introduced to ensure consistent polish across all screens. The modular-monolith structure and couple-level data isolation contract of V1 are preserved without modification.

---

## System Layers (V1 + V1.5 additions)

- **Presentation layer**
  - Mobile app (Expo SDK 52 / React Native 0.76.5): all V1 screens + Budget, OCR Review, AI Chat screens; unified theme tokens from `mobile/src/theme/index.ts`.
  - API controllers: V1 endpoints unchanged; new versioned REST controllers for Budget, OCR, and Chat under `/api/v1/`.
- **Application layer**
  - V1 services unchanged.
  - New use-case services: `BudgetService`, `ImportJobService`, `OcrProcessingService`, `ChatContextService`.
- **Domain layer**
  - V1 entities unchanged.
  - New entities: `BudgetPlan`, `BudgetAllocation`, `ImportJob`, `OcrCandidate`.
- **Infrastructure layer**
  - V1 infrastructure unchanged.
  - New adapters: `AzureDocumentIntelligenceAdapter`, `FirebaseStorageAdapter`, `GeminiChatAdapter`.
  - Docker container packaging; environment variable–based secret injection.

---

## Modules And Responsibilities

### [V1 — unchanged] Auth, Couple, NotificationCapture, Transaction, Goal, CashFlow, Notification
See V1 architecture for full specifications. No breaking changes in V1.5.

---

### Budget Module (new)
- **Folder**: `backend/src/CoupleSync.Application/Budget/`
- **Responsibility**: Manage monthly income declaration and per-category budget allocations. Compute `actual_vs_budget` per category from the existing `Transaction` table. Emit `BUDGET_EXCEEDED` FCM alert when actual spend surpasses allocation.
- **Domain entities**:
  - `BudgetPlan` — `id`, `couple_id`, `month` (YYYY-MM string), `gross_income`, `currency`, `created_at_utc`, `updated_at_utc`. Implements `ICoupleScoped`.
  - `BudgetAllocation` — `id`, `budget_plan_id`, `category` (max 64 chars), `allocated_amount`, `currency`, `created_at_utc`. FK to `BudgetPlan`. Up to 20 allocations per plan.
- **Key operations**:
  - `BudgetService.UpsertPlan(coupleId, month, grossIncome, currency)` — upsert by `(couple_id, month)`.
  - `BudgetService.ReplaceAllocations(coupleId, planId, allocationList)` — transactional replace.
  - `BudgetService.GetCurrentSummary(coupleId, month)` — join `BudgetAllocation` with aggregate `SUM(amount)` from `Transaction` filtered by `couple_id` + current month window.
  - `BudgetService.ComputeGap(grossIncome, allocations)` → `gross_income - SUM(allocated_amount)`; surfaced in summary response.
- **Alert integration**: After each transaction is ingested, `AlertPolicyService` evaluates budget overspend per category and queues a `BUDGET_EXCEEDED` notification event when `actual > allocated`.

---

### OCR Import Module (new)
- **Folder**: `backend/src/CoupleSync.Application/OcrImport/`
- **Responsibility**: Accept file uploads, trigger OCR via Azure AI Document Intelligence, cache raw results, present transaction candidates to the mobile client for review, and merge confirmed candidates into the standard transaction pipeline.
- **Domain entities**:
  - `ImportJob` — `id`, `couple_id`, `user_id`, `storage_path`, `file_mime_type`, `status` (enum: `Pending`, `Processing`, `Ready`, `Failed`), `ocr_result_json`, `created_at_utc`, `updated_at_utc`. Implements `ICoupleScoped`.
  - `OcrCandidate` (value object embedded in `ocr_result_json`) — `index`, `date`, `description`, `amount`, `currency`, `confidence`, `duplicate_suspected`.
- **Upload flow**:
  1. Mobile sends `POST /api/v1/ocr/upload` (multipart, max 10 MB, MIME: `image/jpeg`, `image/png`, `application/pdf`).
  2. Backend validates file type and size, stores file in Firebase Storage at `uploads/{coupleId}/{uploadId}.{ext}` using `FirebaseStorageAdapter`.
  3. `ImportJob` record created with status `Pending`; `upload_id` returned.
  4. Background job picks up the record, changes status to `Processing`, calls `AzureDocumentIntelligenceAdapter`.
  5. Results parsed into `OcrCandidate[]`, deduplication fingerprints computed against existing `Transaction` records, status set to `Ready` (or `Failed`).
  6. Mobile polls `GET /api/v1/ocr/{upload_id}/status` with exponential back-off.
  7. On `Ready`, mobile calls `GET /api/v1/ocr/{upload_id}/results` to receive candidate list.
  8. Mobile presents diff/merge review screen; user selects and optionally edits candidates.
  9. `POST /api/v1/ocr/{upload_id}/confirm` with `selected_indices[]`; backend creates `Transaction` records via existing `TransactionService.CreateFromOcr()`, feeding into the standard categorization, alert, and dashboard pipeline.
- **Deduplication fingerprint**: SHA-256 of `couple_id + date + amount + normalized_description` (lowercased, whitespace collapsed). Candidates with matching fingerprints are flagged `duplicate_suspected=true` but importable.
- **Quota exhausted path**: When Azure AI Document Intelligence returns a 429 or quota error, the job transitions to `Failed` with `error_code=quota_exhausted` and `quota_reset_date`. API returns HTTP 429 to mobile; mobile surfaces recovery guidance.
- **Fallback**: If `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` is absent and `GOOGLE_VISION_API_KEY` is present, `OcrProcessingService` routes to `GoogleCloudVisionAdapter` instead.

---

### AI Chat Module (new — V1.5 last priority)
> **Implementation gate**: Do NOT start until AC-101 through AC-140 are fully verified.

- **Folder**: `backend/src/CoupleSync.Application/AiChat/`
- **Responsibility**: Accept a user chat message with optional spending/budget context, build a system prompt from couple's financial summary, call Gemini Flash 2.0 via Google AI Studio REST API, and return the assistant reply. No server-side chat history persistence.
- **Key operation**: `ChatContextService.BuildSystemPrompt(coupleId, month)` — queries `BudgetService.GetCurrentSummary()` and `TransactionService.GetRecentByCategory(coupleId, days: 30)` to inject live financial context.
- **Gemini call**: `POST https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent` with API key from `GEMINI_API_KEY` env var. Single-turn, no streaming for V1.5.
- **Rate limit handling**: Free tier allows 15 RPM and 1M tokens/day. If 429 is returned, respond with HTTP 429 to mobile; mobile shows "Assistant is busy, try again in a moment."
- **No history storage**: Each request is stateless. Mobile holds conversation history client-side only.

---

## Data Model Additions (V1.5)

V1 tables are unchanged. New tables added via EF Core migrations:

```
budget_plans
  id            UUID PK
  couple_id     UUID NOT NULL  (FK couples.id, index)
  month         VARCHAR(7) NOT NULL   -- YYYY-MM
  gross_income  DECIMAL(18,2) NOT NULL
  currency      VARCHAR(3) NOT NULL
  created_at_utc TIMESTAMPTZ NOT NULL
  updated_at_utc TIMESTAMPTZ NOT NULL
  UNIQUE (couple_id, month)

budget_allocations
  id              UUID PK
  budget_plan_id  UUID NOT NULL  (FK budget_plans.id ON DELETE CASCADE)
  category        VARCHAR(64) NOT NULL
  allocated_amount DECIMAL(18,2) NOT NULL
  currency        VARCHAR(3) NOT NULL
  created_at_utc  TIMESTAMPTZ NOT NULL
  INDEX (budget_plan_id, category)

import_jobs
  id              UUID PK
  couple_id       UUID NOT NULL  (FK couples.id, index)
  user_id         UUID NOT NULL
  storage_path    VARCHAR(512) NOT NULL
  file_mime_type  VARCHAR(64) NOT NULL
  status          VARCHAR(16) NOT NULL  -- Pending|Processing|Ready|Failed
  ocr_result_json JSONB
  error_code      VARCHAR(64)
  error_message   VARCHAR(512)
  created_at_utc  TIMESTAMPTZ NOT NULL
  updated_at_utc  TIMESTAMPTZ NOT NULL
  INDEX (couple_id, status)
```

---

## API Surface (V1.5 additions)

Base path: `/api/v1/`

### Budget Contracts
| Method | Path | Description |
|---|---|---|
| `POST` | `/budgets` | Create or upsert monthly plan (body: `{month, gross_income, currency}`) |
| `GET` | `/budgets/current` | Current month plan with allocations, actual_spent, and budget_gap |
| `GET` | `/budgets/{month}` | Plan for a specific YYYY-MM |
| `PUT` | `/budgets/{planId}/allocations` | Replace allocation list (body: `[{category, allocated_amount, currency}]`) |

**Response shape for GET /budgets/current**:
```json
{
  "plan_id": "...",
  "month": "2026-04",
  "gross_income": 10000.00,
  "currency": "BRL",
  "budget_gap": 3500.00,
  "allocations": [
    { "category": "Food", "allocated_amount": 2000.00, "actual_spent": 1850.00, "remaining": 150.00 }
  ]
}
```

### OCR Import Contracts
| Method | Path | Description |
|---|---|---|
| `POST` | `/ocr/upload` | Multipart upload; returns `{upload_id}` |
| `GET` | `/ocr/{uploadId}/status` | Returns `{status, error_code?, quota_reset_date?}` |
| `GET` | `/ocr/{uploadId}/results` | Returns candidate list when status=Ready |
| `POST` | `/ocr/{uploadId}/confirm` | Body: `{selected_indices: [0, 2, ...]}` — merges selected candidates |

**Candidate shape**:
```json
{
  "index": 0,
  "date": "2026-04-10",
  "description": "Supermercado BH Ltda",
  "amount": 143.50,
  "currency": "BRL",
  "confidence": 0.92,
  "duplicate_suspected": false
}
```

### AI Chat Contracts
| Method | Path | Description |
|---|---|---|
| `POST` | `/chat` | Body: `{message, history: [{role, content}]}` — returns `{reply}` |

---

## Dockerfile Specification

Multi-stage build targeting the `.NET 8` API. File path: `backend/Dockerfile`.

```dockerfile
# Stage 1 — Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY CoupleSync.sln .
COPY src/CoupleSync.Api/CoupleSync.Api.csproj src/CoupleSync.Api/
COPY src/CoupleSync.Application/CoupleSync.Application.csproj src/CoupleSync.Application/
COPY src/CoupleSync.Domain/CoupleSync.Domain.csproj src/CoupleSync.Domain/
COPY src/CoupleSync.Infrastructure/CoupleSync.Infrastructure.csproj src/CoupleSync.Infrastructure/
RUN dotnet restore

COPY . .
RUN dotnet publish src/CoupleSync.Api/CoupleSync.Api.csproj \
    -c Release -o /app/publish --no-restore

# Stage 2 — Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Azure Container Apps uses port 8080 by default
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "CoupleSync.Api.dll"]
```

**Key notes**:
- `ASPNETCORE_URLS=http://+:8080` aligns with Azure Container Apps ingress default.
- The app runs EF Core migrations on startup via `app.MigrateDatabase()` called in `Program.cs` before `app.Run()`.
- No `HEALTHCHECK` directive in Dockerfile; health probes are configured in Azure Container Apps ingress pointing to `/api/v1/health`.
- Target image size < 200 MB using `aspnet:8.0` (not SDK image).

---

## Cloud Deployment Topology (V1.5)

```
┌─────────────────────────────────────────────────────┐
│  Azure Container Apps (Consumption Plan, free tier) │
│                                                     │
│  ┌────────────────────────────────────────────────┐ │
│  │  CoupleSync API container                      │ │
│  │  .NET 8 • port 8080                            │ │
│  │  Secrets sourced from ACA secret store         │ │
│  └────────────────┬───────────────────────────────┘ │
│                   │                                  │
└───────────────────┼──────────────────────────────────┘
                    │ SSL/TLS (Neon requires SslMode=Require)
         ┌──────────▼───────────┐
         │  Neon.tech PostgreSQL │
         │  serverless free tier │
         │  10 GB • ~20 conn     │
         └──────────────────────┘

External integrations:
  Firebase FCM        ← push notifications (V1 unchanged)
  Firebase Storage    ← OCR file uploads (new)
  Azure AI Doc Intel  ← OCR extraction (new)
  Gemini Flash 2.0    ← AI chat (new, last priority)
```

### Connection string pattern for Neon
```
Host=<neon-host>;Database=couplesync;Username=<user>;Password=<pwd>;
SslMode=Require;TrustServerCertificate=true;
MaxPoolSize=10;MinPoolSize=1;ConnectionIdleLifetime=60
```

`MaxPoolSize=10` keeps the app within Neon free-tier's ~20 connection limit, leaving headroom for a second container revision during deployments.

### Environment secrets (Azure Container Apps secret store)
| Secret name | Purpose |
|---|---|
| `DATABASE_URL` | Full Npgsql connection string (Neon) |
| `JWT__SECRET` | JWT signing key |
| `FIREBASE_CREDENTIAL_JSON` | FCM service account JSON (base64) |
| `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` | Azure AI Doc Intelligence endpoint URL |
| `AZURE_DOCUMENT_INTELLIGENCE_KEY` | Azure AI Doc Intelligence API key |
| `FIREBASE_STORAGE_BUCKET` | Firebase Storage bucket name |
| `GOOGLE_VISION_API_KEY` | Optional: fallback OCR key |
| `GEMINI_API_KEY` | Gemini Flash API key (Google AI Studio) |

All secrets are mounted as environment variables in the container; none appear in source code or `appsettings.json`.

---

## CI/CD Pipeline Design

GitHub Actions workflows in `.github/workflows/`:

### `ci.yml` — build and test gate
```
trigger: push/PR to main
steps:
  1. dotnet restore + dotnet build
  2. dotnet test --no-build (must exit 0; 219+ tests)
  3. npx tsc --noEmit (Expo TypeScript check)
  4. trufflehog or git-secrets scan for committed secrets
  5. docker build --no-push (validate Dockerfile builds cleanly)
```

### `deploy.yml` — container deploy (main branch only, after ci.yml passes)
```
trigger: push to main (ci.yml green)
steps:
  1. docker build -t <acr>.azurecr.io/couplesync-api:$SHA .
  2. docker push to Azure Container Registry (or GHCR)
  3. az containerapp update --image <new-tag>
  4. Wait for revision to become active (health probe /api/v1/health)
```

### `mobile-apk.yml` — EAS Build APK (manual trigger or tag)
```
trigger: workflow_dispatch or tag v*
steps:
  1. npm ci in mobile/
  2. eas build --platform android --profile preview --non-interactive
  3. Download APK artifact from EAS
  4. Upload to Firebase App Distribution via firebase-tools
  5. Notify tester group via Firebase App Distribution API
```

---

## Updated Directory Layout (V1.5 additions only)

### backend/ (additions)
```
backend/
  Dockerfile                                     ← NEW
  src/CoupleSync.Application/
    Budget/
      BudgetService.cs
      Commands/ (UpsertPlan, ReplaceAllocations)
      Queries/ (GetCurrentSummary)
    OcrImport/
      ImportJobService.cs
      OcrProcessingService.cs
      Commands/ (StartUpload, ConfirmCandidates)
      Queries/ (GetStatus, GetResults)
    AiChat/
      ChatContextService.cs
      GeminiChatService.cs
  src/CoupleSync.Domain/Entities/
    BudgetPlan.cs
    BudgetAllocation.cs
    ImportJob.cs
  src/CoupleSync.Infrastructure/
    Integrations/
      AzureDocumentIntelligence/
        AzureDocumentIntelligenceAdapter.cs
      FirebaseStorage/
        FirebaseStorageAdapter.cs
      Gemini/
        GeminiChatAdapter.cs
    Persistence/
      Migrations/   (new EF migrations for budget_plans, budget_allocations, import_jobs)
  src/CoupleSync.Api/
    Controllers/
      BudgetController.cs
      OcrController.cs
      ChatController.cs
    Contracts/
      Budget/ (request/response DTOs)
      Ocr/    (request/response DTOs)
      Chat/   (request/response DTOs)
```

### mobile/ (additions)
```
mobile/
  src/
    theme/
      index.ts                    ← NEW (colors, spacing, typography, borderRadius, shadows)
    components/
      LoadingState.tsx            ← NEW
      EmptyState.tsx              ← NEW
      ErrorState.tsx              ← NEW
    modules/
      budget/
        screens/BudgetScreen.tsx
        screens/BudgetSetupScreen.tsx
        hooks/useBudget.ts
        api/budgetApi.ts
      ocr/
        screens/OcrUploadScreen.tsx
        screens/OcrReviewScreen.tsx
        hooks/useOcrImport.ts
        api/ocrApi.ts
      chat/
        screens/ChatScreen.tsx
        hooks/useChat.ts
        api/chatApi.ts
  app/(main)/
    budget.tsx                    ← NEW tab screen
    ocr-review.tsx                ← NEW screen
    chat.tsx                      ← NEW tab screen (last priority)
  eas.json                        ← NEW (EAS Build profiles)
  .github/workflows/
    mobile-apk.yml                ← NEW
    ci.yml                        ← NEW (or update existing)
    deploy.yml                    ← NEW
```

---

## Data Flow (V1.5 additions)

### Budget summary flow
1. GET `/api/v1/budgets/current` received; `couple_id` extracted from JWT.
2. `BudgetService.GetCurrentSummary()` loads `BudgetPlan` (current month) + its `BudgetAllocation` rows.
3. Raw SQL/LINQ aggregation: `SELECT category, SUM(amount) FROM transactions WHERE couple_id=? AND event_timestamp_utc >= month_start AND < month_end GROUP BY category`.
4. Join allocations with aggregated actuals; compute `remaining = allocated - actual`, `budget_gap = gross_income - SUM(allocated)`.
5. Response serialized and returned.

### OCR import flow
1. Mobile uploads file → `POST /api/v1/ocr/upload` → `FirebaseStorageAdapter.Upload(coupleId, uploadId, stream)` → `ImportJob` created (Pending).
2. `OcrBackgroundJob` picks up Pending jobs; calls `AzureDocumentIntelligenceAdapter.AnalyzeAsync(storageUrl)`.
3. Adapter response parsed into `OcrCandidate[]`; deduplication fingerprints compared to `Transaction` table.
4. `ImportJob.ocr_result_json` updated; status → `Ready`.
5. Mobile presents review screen; user confirms selection.
6. `POST /api/v1/ocr/{uploadId}/confirm` → `TransactionService.CreateFromOcrCandidates(coupleId, userId, selectedCandidates)`.
7. New transactions enter standard pipeline: categorization → alert evaluation → dashboard aggregation.

### AI Chat flow
1. Mobile sends `POST /api/v1/chat` with `{message, history[]}`.
2. `ChatContextService.BuildSystemPrompt(coupleId)` fetches budget summary + 30-day category totals.
3. `GeminiChatAdapter.SendAsync(systemPrompt, history, message)` calls Gemini Flash 2.0 API.
4. Reply text returned; mobile appends to client-side conversation history.

---

## Internal Events (V1.5 additions)
- `BudgetExceededDetected` — raised by `AlertPolicyService` when `actual > allocated`; handled by `NotificationDispatcher` to send `BUDGET_EXCEEDED` FCM.
- `ImportJobCompleted` — raised when OCR processing finishes; no external consumer, ImportJob status is polled.
- `ImportJobFailed` — raised on OCR error; sets `ImportJob.status = Failed`.

---

## Error Handling Strategy

All V1 error patterns preserved. V1.5 additions:
- `BUDGET_NOT_FOUND`: 404 when no plan exists for requested month.
- `BUDGET_ALLOCATION_LIMIT`: 422 when attempting to create more than 20 allocations per plan.
- `OCR_UPLOAD_TOO_LARGE`: 413 when file exceeds 10 MB.
- `OCR_UNSUPPORTED_TYPE`: 415 for MIME types outside allowed list.
- `OCR_QUOTA_EXHAUSTED`: 429 with `quota_reset_date` when Azure AI Document Intelligence monthly quota reached.
- `OCR_JOB_NOT_READY`: 409 when `GET /ocr/{id}/results` called while status is still `Processing`.
- `CHAT_RATE_LIMITED`: 429 when Gemini returns 429; message surfaced in mobile UI.
- All new error codes use existing `code / message / details / traceId` envelope.

---

## Configuration Strategy (V1.5 additions)

New strongly-typed options classes:
- `BudgetOptions`: `MaxAllocationsPerPlan` (default 20).
- `OcrOptions`: `MaxFileSizeBytes` (default 10_485_760), `AllowedMimeTypes`, `StorageProvider` (`FirebaseStorage` | `AzureBlob`), `ProcessingProvider` (`AzureDocumentIntelligence` | `GoogleVision`).
- `GeminiOptions`: `Endpoint`, `Model` (default `gemini-2.0-flash`), `MaxTokens` (default 1024).

`DATABASE_URL` is parsed directly by Npgsql; no intermediate `DatabaseOptions` class adds indirection.

---

## Security Considerations (V1.5 additions)

- All new endpoints enforce `couple_id` from JWT — no client-supplied `couple_id` accepted.
- `ImportJob.storage_path` is never returned to the client; only `upload_id` is exposed. Mobile cannot derive storage paths.
- OCR file uploads: content-type validated server-side against file magic bytes, not just MIME header.
- Gemini API key stored in Azure Container Apps secret; never logged. Request/response bodies containing financial data are partially redacted in structured logs.
- Azure AI Document Intelligence is called with a managed key stored as ACA secret; no API key appears in source.
- Firebase Storage rules enforce `couple_id`-prefixed paths; no read/write outside own prefix.
- CI secret scan (`trufflehog`) blocks merge if any new credential patterns are detected in the diff.
- `OcrCandidate.description` and `OcrCandidate.amount` are validated/sanitized before `Transaction` creation to prevent injection through OCR output.

---

## Testing Strategy Overview (V1.5 additions)

- **Unit tests**: `BudgetService` aggregation math, fingerprint dedup logic, OCR candidate parsing, Gemini prompt builder.
- **Integration tests**: Budget CRUD + couple isolation (existing SQLite WebApplicationFactory pattern); OCR endpoint with mocked `IOcrProvider` and `IStorageAdapter` to avoid real Azure/Firebase calls; Chat endpoint with mocked `IGeminiAdapter`.
- **Background job tests**: OCR job state machine (`Pending → Processing → Ready/Failed`) with mocked provider.
- **Security tests**: Cross-couple access on all new endpoints returns 403 (integration tests).
- **Manual acceptance tests**: Map to AC-101 – AC-150 per `acceptance.json`.

---

## Minimal Integration Plan (V1.5)

1. Create `backend/Dockerfile` and verify `docker build` succeeds locally.
2. Set up Neon.tech free-tier project; update `DATABASE_URL` in local `.env.local`; confirm `dotnet test` passes against Neon.
3. Add EF Core migrations for `budget_plans`, `budget_allocations`, `import_jobs`.
4. Implement Budget module (entities → application service → controller) with full integration tests.
5. Implement OCR Import module with mocked adapters; add `FirebaseStorageAdapter` and `AzureDocumentIntelligenceAdapter` behind interfaces.
6. Implement mobile theme system, `LoadingState`, `EmptyState`, `ErrorState` components.
7. Implement Budget mobile screens wired to API.
8. Implement OCR Upload + Review mobile screens.
9. Configure Azure Container Apps + push Dockerfile to registry; verify health probe.
10. Configure EAS Build `eas.json` + Firebase App Distribution; generate first test APK.
11. Add GitHub Actions CI/CD workflows.
12. *(Last)* Implement AI Chat module (backend + mobile) only after AC-101–AC-140 verified.
