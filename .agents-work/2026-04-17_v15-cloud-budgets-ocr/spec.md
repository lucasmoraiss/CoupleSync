# CoupleSync V1.5 — Cloud Deployment, Budget Management, OCR Import, UI Polish, AI Chat

## Problem Statement
CoupleSync V1 is functionally complete (219 passing tests) and runs locally. V1.5 must migrate the product to cloud infrastructure that remains 100% free tier, then deliver three high-value features (budget management, OCR receipt/statement import, UI polish), with an AI financial chat assistant as the last and lowest-priority item. All five areas must coexist without disrupting V1 behavior or breaking the existing test suite.

## Goals
- Run backend API and database fully on Azure free infrastructure (Azure Container Apps + Neon.tech PostgreSQL).
- Deliver APKs to pilot testers via EAS Build + Firebase App Distribution with zero manual Gradle setup.
- Give couples a clear monthly budget framework tied to real spending per category.
- Allow couples to import bank statements and receipts by photographing or uploading a PDF, review and merge extracted transactions.
- Consolidate the mobile design system and add loading/empty/error states and micro-interactions across all screens.
- Add a Gemini Flash financial chat assistant (last priority, only after all other V1.5 features are complete).
- Keep every feature on 100% free cloud tiers.

## Non-Goals
- iOS support.
- Multi-region cloud deployment.
- Paid tiers on any cloud service.
- Open Finance / bank OAuth integration.
- Rewriting existing backend architecture (modular monolith stays).
- Real-time bank balance sync.
- Server-side chat history persistence.
- Advanced AI categorization beyond what exists in V1.

## Scope Boundary: V1.5 vs V2
V2 scope (explicitly deferred): multi-couple sharing, iOS, microservices decomposition, advanced ML categorization, recurring payment automation, investment tracking.  
AI Chat (AC-141–AC-150) is marked V1.5-last — it is part of this session but MUST be the final item implemented, scheduled only after AC-101–AC-140 are complete and verified.

## V1 Reference
- Existing session: `.agents-work/2026-04-13_couplesync-ai-driven-bootstrap/`
- V1 acceptance criteria: AC-001 through AC-012 (fully implemented, 219 tests passing).
- Stack: .NET 8 Web API (EF Core + PostgreSQL), Expo SDK 52 / React Native 0.76.5, Firebase FCM, dark theme.

---

## Area 1 — Cloud Deployment

### User Stories
- As DevOps, I want the backend API to be containerized and running on Azure Container Apps so that the pilot reaches internet-reachable testers without a local server.
- As DevOps, I want the database to run on Neon.tech so that we have a managed PostgreSQL instance at zero monthly cost with automated backups.
- As a tester, I want to install the app from a Firebase App Distribution link so that I don't need to connect via USB or use Play Store.
- As a developer, I want all cloud secrets in environment variables so that no credentials are ever committed to the repository.

### Functional Requirements
- FR-101: Containerize backend API as a Docker image. Dockerfile in `backend/`.
- FR-102: Deploy to Azure Container Apps Consumption Plan (free tier) via GitHub Actions workflow.
- FR-103: Run EF Core migrations on application startup against Neon.tech PostgreSQL (connection string via env var `DATABASE_URL`).
- FR-104: Store all runtime secrets (DATABASE_URL, JWT secret, FCM credentials, OCR API keys) as Azure Container Apps secrets exposed as environment variables.
- FR-105: EAS Build with `preview` profile (`buildType: apk`) produces an installable APK.
- FR-106: GitHub Actions workflow uploads APK artifact and triggers Firebase App Distribution release to pilot tester group.
- FR-107: `/health` (or `/api/v1/health`) endpoint returns HTTP 200 with service status JSON; Azure ingress uses this as liveness probe.
- FR-108: CI pipeline runs `dotnet test` and Expo type checks; deployment blocked if any step fails.
- FR-109: Couple-level data isolation rules are unchanged and verified by existing integration tests against Neon.

### Non-Functional Requirements
- NFR-101: Cold-start latency < 5 seconds for first request after scale-to-zero event.
- NFR-102: Neon free tier connection pool ≤ 20 concurrent connections; application must use connection pooling (Npgsql pooling, max pool size 10).
- NFR-103: Docker image < 200 MB (alpine or distroless base).
- NFR-104: No secrets committed to repository (enforced by `git-secrets` or equivalent scan in CI).

---

## Area 2 — Budget Management

### User Stories
- As a couple, we want to set our monthly gross income so that the app knows our budget ceiling.
- As a couple, we want to allocate monthly budget amounts per spending category so we can plan intentionally.
- As Partner A or B, I want to see how much we have left in each category this month so I make spending decisions with confidence.
- As a couple, we want an alert when we overspend a category so we can adjust quickly.
- As a couple, we want to see unallocated budget (budget gaps) so we know if we are under-planning.

### Functional Requirements
- FR-111: New `BudgetPlan` entity: `id`, `couple_id`, `month` (YYYY-MM), `gross_income`, `created_at`, `updated_at`.
- FR-112: New `BudgetAllocation` entity: `id`, `budget_plan_id`, `category`, `allocated_amount`, `scope` (couple | individual_user_id).
- FR-113: `GET /api/v1/budgets/current` returns current month's plan with allocations and per-category actual spending.
- FR-114: `POST /api/v1/budgets` creates or updates current month's plan (upsert by couple_id + month).
- FR-115: `PUT /api/v1/budgets/{id}/allocations` replaces allocation list for a budget plan.
- FR-116: Budget summary computes `actual_vs_budget` per category using transactions within the current calendar month.
- FR-117: Overspend condition triggers an FCM alert of type `BUDGET_EXCEEDED` when actual > allocated for a category.
- FR-118: Both partners see identical budget and spending data (couple-scoped, no individual-only budget view in V1.5).
- FR-119: `budget_gap` = `gross_income` − sum of all `allocated_amount`; surfaced in `GET /api/v1/budgets/current` response.
- FR-120: All budget endpoints require authenticated user with `couple_id` claim and enforce couple-level isolation.

### Non-Functional Requirements
- NFR-111: Budget summary response < 1 second (indexed by `couple_id` + `month`).
- NFR-112: Budget allocation list supports up to 20 categories without pagination.

---

## Area 3 — OCR Import

### User Stories
- As a user, I want to photograph a paper bank statement so that I can import its transactions without typing them manually.
- As a user, I want to upload a PDF bank statement so that past history is available in the app.
- As a user, I want to review extracted transactions before they are saved so that errors from OCR do not pollute my history.
- As a user, I want already-imported transactions to be skipped automatically so I don't get duplicates.

### Functional Requirements
- FR-121: Mobile provides camera capture and file-picker (image or PDF) UI on the Transactions screen.
- FR-122: File uploaded via authenticated `POST /api/v1/ocr/upload`; backend stores file in Firebase Storage (or Azure Blob if Storage bucket is simpler); returns `upload_id`.
- FR-123: Backend triggers OCR processing via Azure AI Document Intelligence prebuilt-invoice or prebuilt-receipt model (5,000 pages/month free); falls back to Google Cloud Vision if configured.
- FR-124: `GET /api/v1/ocr/{upload_id}/results` returns extracted transaction candidates: `date`, `description`, `amount`, `currency`, `confidence`.
- FR-125: Mobile displays diff/merge review screen: list of candidates with checkbox selection and editable fields.
- FR-126: `POST /api/v1/ocr/{upload_id}/confirm` accepts `selected_indices[]`; backend creates transactions for selected items.
- FR-127: Deduplication uses fingerprint (`couple_id` + `date` + `amount` + normalized `description`); duplicate candidates are flagged but not blocked (user may override).
- FR-128: Imported transactions enter the standard transaction pipeline (categorization, alert rules, dashboard aggregation).
- FR-129: All OCR endpoints enforce couple-level authorization (`couple_id` from JWT).
- FR-130: OCR status machine: `PENDING` → `PROCESSING` → `READY` | `FAILED`; mobile polls `GET /api/v1/ocr/{upload_id}/status` with exponential back-off.
- FR-131: Graceful degradation when Azure AI Document Intelligence free quota (5,000 pages/month) is exhausted — return HTTP 429 with `quota_exhausted` error code; mobile surfaces recovery guidance.

### Non-Functional Requirements
- NFR-121: OCR processing latency target < 30 seconds for a single-page document under normal conditions.
- NFR-122: Uploaded files are stored with couple-scoped path (`/{couple_id}/{upload_id}/`) to enforce isolation at storage layer.
- NFR-123: File size limit: 10 MB per upload.
- NFR-124: Accepted MIME types: `image/jpeg`, `image/png`, `application/pdf`.

---

## Area 4 — UI/UX Polish

### User Stories
- As a user, I want consistent visual styling across all screens so the app feels professional.
- As a user, I want loading indicators on every async operation so I know the app is working.
- As a user, I want helpful empty states that guide me toward the next action when no data exists.
- As a user, I want clear error messages with retry actions so I am not left stuck.
- As a user, I want subtle haptic feedback on important actions so interactions feel responsive.

### Functional Requirements
- FR-141: Create `mobile/src/theme/index.ts` exporting `colors`, `spacing`, `typography`, `borderRadius`, `shadows` tokens.
- FR-142: All existing screen components import theme tokens from `mobile/src/theme/index.ts`; no hardcoded hex values outside theme file.
- FR-143: Add `<LoadingState />` component (spinner + optional message); used on all data-fetching screens.
- FR-144: Add `<EmptyState />` component (icon + title + subtitle + optional CTA); used on Goals, Transactions, Budget, and Dashboard when no data exists.
- FR-145: Add `<ErrorState />` component (icon + message + retry button); used on all async screens.
- FR-146: Apply `react-native-reanimated` fade/slide transitions between main tab screens.
- FR-147: Apply `expo-haptics` `impactAsync(ImpactFeedbackStyle.Medium)` on primary CTA presses and alert acknowledgements.
- FR-148: All tap targets ≥ 48 dp (Android Material guidance).
- FR-149: Bottom tab navigation uses large tap targets and follows Android-first ergonomics.
- FR-150: Polish is applied across: Login, Register, Couple Setup, Dashboard, Transactions, Goals, Budget (new), OCR Review (new), AI Chat (new).

### Non-Functional Requirements
- NFR-141: No additional bundle size increase > 200 KB from animation libraries.
- NFR-142: All colors accessible (WCAG AA contrast ratio ≥ 4.5:1) in dark theme.

---

## Area 5 — AI Financial Chat (V1.5-last, V2 boundary)

> **Implementation gate**: AI Chat MUST NOT be started until AC-101 through AC-140 (Areas 1–4) are fully verified.

### User Stories
- As a user, I want to ask the app "where can we eat tonight under R$60?" and get suggestions within my current budget.
- As a user, I want the assistant to know my spending context so suggestions are realistic and relevant to my situation.
- As a developer, I want the AI API key stored in an environment variable so it is never committed.

### Functional Requirements
- FR-151: New `AI Chat` tab in mobile bottom navigation (appears after all other V1.5 tabs).
- FR-152: Mobile sends chat messages to `POST /api/v1/ai/chat`; backend proxies to Gemini Flash API (`gemini-1.5-flash` or latest free-tier model via Google AI Studio).
- FR-153: System prompt includes: current month's budget summary, category spending totals, couple city (Belo Horizonte), and today's date.
- FR-154: Chat responses are streamed or returned as a single turn; V1.5 does not persist chat history server-side.
- FR-155: Feature flag `AI_CHAT_ENABLED` (env var, default `false`); when false, tab is hidden and endpoint returns 404.
- FR-156: Backend applies per-couple rate limiting to AI chat endpoint (max 30 requests/hour) to prevent free-tier quota exhaustion.
- FR-157: Gemini API key stored as `GEMINI_API_KEY` environment variable; never committed.
- FR-158: If Gemini returns `429` or quota error, mobile surfaces "assistant unavailable, try again later" message.
- FR-159: Responses framing investment advice or legal guidance redirect user to consult a professional.

### Non-Functional Requirements
- NFR-151: Chat round-trip latency < 5 seconds for typical single-turn request.
- NFR-152: Free-tier daily token limit (~1M tokens/day Gemini Flash) monitored; quota alert logged when > 80% consumed.

---

## Edge Cases
1. **Cloud cold-start timeout** — Container App scales to zero after inactivity. First request may exceed 5 s cold-start target. Liveness probe must not restart pod during boot; health route excluded from ingress timeout.
2. **Neon connection limit** — Neon free tier allows ~20 connections. Concurrent requests exceeding pool size queue or fail. Max pool size must be configured to ≤ 10; EF Core must not create unbounded pools.
3. **Budget month boundary** — User opens app just after midnight on the 1st; stale budget cache from prior month may be displayed. Budget summary must include `month` field and client must re-fetch on foreground after date change.
4. **Budget category deleted with existing transactions** — Transactions reference a category by string key. Budget allocation references same key. Deleting or renaming a category must not cascade-delete historical transactions; orphaned category values are surfaced as "Uncategorized".
5. **Concurrent budget edits** — Both partners update budget allocations simultaneously. Backend must use optimistic concurrency (ETag / `updated_at` check) or last-write-wins with idempotent upsert clearly documented.
6. **OCR multi-page PDF** — Azure AI Document Intelligence may partially process a multi-page PDF (e.g., page 3 fails while pages 1–2 succeed). Backend must return partial results with per-page `confidence` and surface which pages were skipped.
7. **OCR handwritten or low-quality scan** — Confidence score below threshold (< 0.6). Candidates are still shown but flagged with a warning; user may edit before confirming.
8. **OCR duplicate import attempt** — Same PDF reuploaded. Fingerprint collision on extracted transactions must flag them as `DUPLICATE_SUSPECTED` in the review screen; user may still choose to import or skip.
9. **OCR free quota exhausted (5,000 pages/month)** — Azure AI Document Intelligence returns 429. Backend returns `quota_exhausted` error code; mobile shows "OCR unavailable this month, quota reached. Try again on [date]" with exact reset date computed from Azure plan cycle.
10. **APK build cache stale** — EAS Build cache produces incorrect APK after a native dependency update. Build must include `--clear-cache` flag or cache invalidation step when native deps change.
11. **Firebase App Distribution tester group empty** — CI uploads APK but no testers defined. Workflow should warn but not fail; tester group name stored in repo secrets.
12. **AI chat with sensitive input** — User asks for investment or legal advice. System prompt must include a disclaimer redirect. Backend should not modify or suppress the user message itself.
13. **Gemini Flash model deprecation** — Model ID changes. `GEMINI_MODEL` must be a separate env var with a default value that can be updated without redeployment.
14. **Theme module missing token** — Developer adds a new screen without using theme tokens. CI lint step should enforce no hardcoded hex values in `.tsx` files outside `theme/index.ts`.

---

## Assumptions
- Azure Container Apps Consumption Plan remains free at pilot scale (< 250,000 requests/month, < 180,000 vCPU-sec/month).
- Neon.tech free tier (0.25 compute unit, 0.5 GB RAM, 10 GB storage, 1 project) is sufficient for up to 10 users.
- Firebase Storage free tier (1 GB storage, 50K downloads/day) is sufficient for OCR uploads at pilot scale.
- Azure AI Document Intelligence free tier (5,000 pages/month) is sufficient for pilot; PDF statements average 1–5 pages.
- Google AI Studio Gemini Flash free tier (up to 1M tokens/day, 60 RPM) is sufficient for casual use by 5 couples.
- EAS Build free tier (30 iOS / 30 Android builds per month) is sufficient for development iterations.
- V1.5 does not require retroactive budget history; budgets track forward from creation date.
- `react-native-reanimated` (already in many Expo projects) and `expo-haptics` are acceptable additions.
- Chat history is ephemeral (client-session only); no GDPR/data retention concern for V1.5.
- CI/CD provider is GitHub Actions (free for public repos or small private repo usage).
- Pilot testers are configured in Firebase App Distribution before APK distribution begins.

---

## Definition of Done
- All five areas have backend API changes covered by integration tests and mobile screens implemented and manually verified.
- AC-101 through AC-150 are all verifiable and verified (AI chat ACs verified last).
- CI pipeline is green (build + test + deploy) on `main` branch using Azure Container Apps.
- Backend deploys to Azure Container Apps and connects to Neon.tech without errors.
- APK is generated by EAS Build and distributed via Firebase App Distribution.
- No hardcoded secrets in repository (CI secret scan passes).
- `status.json` is updated to `DONE` with all acceptance checks recorded, `last_update` timestamp current.
- Session artifacts (`spec.md`, `acceptance.json`, `tasks.yaml`, `status.json`) are consistent.

## Acceptance Criteria
See `acceptance.json` for machine-readable AC-101 through AC-150 mapped to verification steps.
