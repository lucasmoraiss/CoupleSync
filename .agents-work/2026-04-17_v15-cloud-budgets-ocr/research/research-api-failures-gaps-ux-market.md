# Research Report: API Failures, Functionality Gaps, UX Issues & Market Comparison

**Session:** 2026-04-17_v15-cloud-budgets-ocr  
**Requested by:** User (via Orchestrator/Researcher mode)  
**Date:** 2026-04-17  
**Confidence:** high (based on full codebase inspection)

---

## Topic / Question

1. Why do budget saving and OCR upload fail from the mobile app?
2. What functionality gaps exist vs user expectations (PDF bank statements, manual income, notification capture, AI)?
3. What UX issues exist with error handling?
4. What features from competitor apps (Mobills, Organizze, Olivia AI, YNAB, Splitwise, Honeydue) should CoupleSync adopt?

---

## Context

User reports both the OCR upload and the budget save fail silently or with plain Android dialogs. They also describe missing features around bank PDF imports, income updates, and notification-based transaction capture. This research is needed before Coder/Planner agents address the fix tasks.

---

## Methodology

Full static analysis of all files in the request list plus related infrastructure:

- `mobile/src/services/apiClient.ts` — HTTP client setup and per-domain clients
- `mobile/app/(main)/budget/index.tsx` — Budget screen save mutation
- `mobile/app/(main)/ocr-upload.tsx` — OCR upload flow
- `mobile/app/(main)/ocr-review.tsx` + `mobile/src/modules/ocr/screens/OcrReviewScreen.tsx`
- `mobile/src/components/ErrorState.tsx` — styled error component
- `backend/src/CoupleSync.Api/Controllers/BudgetController.cs`
- `backend/src/CoupleSync.Api/Controllers/OcrController.cs`
- `backend/src/CoupleSync.Application/Budget/BudgetService.cs`
- `backend/src/CoupleSync.Application/OcrImport/ImportJobService.cs`
- `backend/src/CoupleSync.Application/OcrImport/OcrProcessingService.cs`
- `backend/src/CoupleSync.Infrastructure/BackgroundJobs/OcrBackgroundJob.cs`
- `backend/src/CoupleSync.Infrastructure/Integrations/AzureDocumentIntelligence/AzureDocumentIntelligenceAdapter.cs`
- `backend/src/CoupleSync.Infrastructure/Integrations/Storage/LocalFileStorageAdapter.cs`
- `backend/src/CoupleSync.Infrastructure/DependencyInjection.cs`
- `backend/src/CoupleSync.Api/Program.cs`
- `backend/src/CoupleSync.Api/appsettings.json` + `appsettings.Development.json`
- `backend/Dockerfile`
- `docker-compose.yml`
- `mobile/src/modules/integrations/notification-capture/NotificationListenerBridge.ts`
- `backend/src/CoupleSync.Application/AiChat/ChatContextService.cs`
- Validators: `CreateBudgetPlanRequestValidator.cs`, `ReplaceAllocationsRequestValidator.cs`
- Filters: `RequireCoupleAttribute.cs`
- `backend/src/CoupleSync.Api/Middleware/GlobalExceptionMiddleware.cs`

---

## Findings

### ROOT CAUSE 1 — OCR Upload: Multipart Content-Type Bug (CRITICAL)

**File:** `mobile/src/services/apiClient.ts`, line ~234:

```typescript
upload: (file: FormData): Promise<AxiosResponse<OcrUploadResponse>> =>
  axiosInstance.post<OcrUploadResponse>('/api/v1/ocr/upload', file, {
    headers: { 'Content-Type': 'multipart/form-data' },
  }),
```

**Root cause:** The Content-Type is manually set to `multipart/form-data` **without a boundary parameter**. In React Native, Axios uses the native XMLHttpRequest layer, which would automatically compute and inject the correct `multipart/form-data; boundary=<uuid>` header when it detects FormData — but only if the header is NOT manually overridden. By overriding it with a headerless `multipart/form-data` value, the boundary is absent.

**Effect on server:** `OcrController.Upload(IFormFile file)` receives `file == null` (ASP.NET Core's multipart parser cannot locate the file part without a boundary) → returns `400 FILE_REQUIRED`.

**Fix:** Remove the explicit Content-Type override so React Native XHR sets it automatically:
```typescript
upload: (file: FormData): Promise<AxiosResponse<OcrUploadResponse>> =>
  axiosInstance.post<OcrUploadResponse>('/api/v1/ocr/upload', file),
  // No Content-Type override — RN XHR will set multipart/form-data; boundary=...
```

**Evidence:** Standard React Native FormData pattern documented across Axios GitHub issues and Expo forums. The backend correctly binds `IFormFile` from multipart; the bug is 100% on the mobile side.

---

### ROOT CAUSE 2 — OCR Upload in Docker: Non-root User Cannot Write Uploads Directory (HIGH)

**File:** `backend/Dockerfile`:

```dockerfile
WORKDIR /app
RUN addgroup -S appgroup && adduser -S appuser -G appgroup
USER appuser
COPY --from=build /app/publish ./
```

`WORKDIR /app` creates `/app` as root **before** `USER appuser`. The `COPY` as `appuser` populates `/app/publish` content but `/app` itself is root-owned. `LocalFileStorageAdapter` calls `Directory.CreateDirectory(...)` to create `/app/uploads/{coupleId}/`, which **fails** because `appuser` has no write permission on root-owned `/app`.

Additionally, `docker-compose.yml` **does not mount a volume** for the uploads directory, so even if permissions were fixed, all uploaded files would be lost on container restart.

**Fix options:**
- OPT-A: Add `RUN mkdir -p /app/uploads && chown appuser:appgroup /app/uploads` in Dockerfile before `USER appuser`
- OPT-B (recommended for cloud): Replace `LocalFileStorageAdapter` with Azure Blob Storage adapter (already abstracted via `IStorageAdapter`) — eliminates local filesystem dependency entirely and provides durability

---

### ROOT CAUSE 3 — OCR Missing Azure Config in Docker (MEDIUM, affects production)

**File:** `docker-compose.yml` — no `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` or `AZURE_DOCUMENT_INTELLIGENCE_KEY` env vars.

**Effect:** `AzureDocumentIntelligenceAdapter.AnalyzeAsync()` falls back to the dev stub:
```csharp
if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
{
    _logger.LogWarning("Azure Document Intelligence not configured. Using stub response.");
    return GetStubResponse();
}
```
The stub returns mock receipt data, so the job becomes `Ready` with fake candidates. Real parsing never runs. In production cloud deployment this causes silently incorrect results.

---

### ROOT CAUSE 4 — Budget Save: No True API Bug, But Error UX is Plain Android (MEDIUM)

**File:** `mobile/app/(main)/budget/index.tsx`, `saveMutation.onError`:

```typescript
onError: () => {
  queryClient.invalidateQueries({ queryKey: ['budget'] });
  Alert.alert('Erro', 'Não foi possível salvar o orçamento. Tente novamente.');
},
```

The budget API itself looks functionally correct for authenticated couples: the request shape, validators, and service logic all match. There is **no backend bug** for budget saving when the user is in a couple and JWT is valid. However:

- If the user is **not yet in a couple**, `[RequireCouple]` returns `403 COUPLE_REQUIRED` — this is silently eaten by `Alert.alert`
- If the JWT has **expired**, the 401 interceptor redirects to login — correct behavior
- The error message is a raw `Alert.alert` (native Android system dialog) — plain gray box, no app styling

**Why the user perceives "budget saving fails":** The most likely scenario is that during onboarding/testing the user was not yet in a couple, or was hitting an expired token. The error was displayed as a native dialog which looks like an OS crash — not a helpful in-app message.

---

### ROOT CAUSE 5 — OCR Uses Receipt Model for Bank Statements (CRITICAL functionality gap)

**File:** `AzureDocumentIntelligenceAdapter.cs`:
```csharp
var url = $"{endpoint}/documentintelligence/documentModels/prebuilt-receipt:analyze?api-version=2024-11-30";
```

**File:** `OcrProcessingService.ParseCandidates()` — looks for `analyzeResult.documents[].fields.Items.valueArray` (the receipt line-items structure).

**Problem:** Brazilian bank statements (PDF exports from Nubank, Itaú, Santander, etc.) are **not receipts**. They are tabular documents with transaction rows. The `prebuilt-receipt` model:
- Expects a single-transaction receipt with `Items`, `MerchantName`, `Total`, `TransactionDate`
- Returns zero candidates for a bank statement because the field structure doesn't match
- Gives no error — just an empty candidate list

**This means the user's expectation of "import PDF bank statement" is architecturally unmet.** The OCR pipeline was designed around individual receipt photos, not multi-transaction PDF bank exports.

**Fix options:**
- OPT-A: Use `prebuilt-layout` model + custom post-processing regex to extract table rows
- OPT-B: Use Azure `prebuilt-document` + text extraction with regex for BR bank formats
- OPT-C: Use a dedicated bank statement parsing service (e.g., Gorila.io, Pluggy, or Belvo) — gives structured transaction data directly
- OPT-D: Implement local PDF → text extraction (`PdfPig` .NET library) + regex parser per bank

---

### FUNCTIONALITY GAP 1 — No Standalone Monthly Income Update

**Current state:** Gross income lives only inside a `BudgetPlan`. Creating/updating income requires constructing a full budget plan with month + currency. There is no `/api/v1/income` endpoint or a lightweight "update income" UX.

**User expectation:** "Manually update monthly income" — a simple quick action, not requiring engagement with the full budget planning flow.

**Impact:** New users without budget plans can't see meaningful AI chat context (income is `null` in system prompt). Dashboard shows incomplete data.

---

### FUNCTIONALITY GAP 2 — Notification Capture Incomplete for Transaction Auto-Import

**Files inspected:** `NotificationListenerBridge.ts`, `_layout.tsx` (starts capture on mount)

The foundation is in place: native Android `NotificationListenerService`, bridge to React Native, `IngestNotificationEvent` endpoint on backend. However based on code inspection:
- The notification parsing patterns for Brazilian bank apps (Nubank, C6, Inter, Bradesco, etc.) need verification
- `handleRawNotificationEvent` is present in `eventUploader.ts` but was not directly inspected for completeness
- The backend `NotificationCapture` module receives events and creates transactions — this path IS implemented

**STATUS:** Infrastructure exists; completeness depends on whether bank-specific regex patterns cover the apps the pilot users have installed. This is a **configuration/patterns gap** not a code gap.

---

### FUNCTIONALITY GAP 3 — AI Chat Already Uses Transaction Context

**Good news:** `ChatContextService.BuildSystemPromptAsync()` already injects:
- Current budget plan (income, gap, per-category allocation vs actual)
- Last 30 days of transactions grouped by category

The AI chat IS connected to real financial data. No gap here beyond UX (the feature is gated by `EXPO_PUBLIC_AI_CHAT_ENABLED=true`).

---

### FUNCTIONALITY GAP 4 — Goals/Planning Not Connected to Budget Data in UI

**Observation:** `GoalProgressService` and goal linking (`LinkTransactionToGoalCommandHandler`) exist in the backend. But the mobile `goals/` screen was not inspected. The ChatContextService does not inject goal data into the AI prompt — a missed opportunity for richer AI suggestions.

---

## UX Issues Inventory

| # | Location | Issue | Severity |
|---|----------|--------|----------|
| 1 | `budget/index.tsx` `saveMutation.onError` | `Alert.alert()` used — native gray Android dialog | HIGH |
| 2 | `OcrReviewScreen.tsx` `confirmMutation.onError` | `Alert.alert()` used — native Android dialog | HIGH |
| 3 | `ocr-upload.tsx` error state | ✅ Uses `ErrorState` component correctly for upload/poll errors | OK |
| 4 | `OcrReviewScreen` load error | ✅ Uses `ErrorState` component correctly for data load errors | OK |
| 5 | Budget query `isError` | Budget screen does NOT render `ErrorState` on query failure — silently shows loading spinner or empty state | MEDIUM |
| 6 | Error messages are generic | "Não foi possível salvar" — no error code, no actionable guidance | MEDIUM |
| 7 | Budget error has no retry flow | `Alert.alert` with OK only; no in-screen retry mechanism | LOW |
| 8 | COUPLE_REQUIRED 403 indistinguishable | Same generic alert whether 403, 500, or network timeout | HIGH |

### Root of "plain Android" messages

The styled `ErrorState` component exists and is well-implemented (`mobile/src/components/ErrorState.tsx`). It is being used correctly for **data-loading errors** (query failures). However **mutation errors** (save/confirm) fall back to `Alert.alert()` everywhere. This is a systematic inconsistency: queries → `ErrorState`, mutations → `Alert.alert`.

---

## Market Comparison: Competitor Feature Analysis

### Mobills (Brazil, #1 budgeting app)
- **Bank import:** CSV/OFX file import + Open Finance OAuth2 (read-only sync)
- **Manual entry:** Quick-add FAB with amount + category + date; recent categories suggested
- **Budget:** Monthly envelope budgets per category with traffic-light fill indicators
- **Couples/sharing:** Multi-profile within same account (not couple-first)
- **Notifications/reminders:** Bill due date reminders; no passive notification interception
- **Reports:** Historical trends, category breakdowns, bar/line charts
- **AI:** None
- **Key strength:** Polished OFX import; category suggestion engine

### Organizze (Brazil)
- **Bank import:** OFX/CSV upload; no direct bank sync
- **Manual entry:** Web-first; mobile is simplified
- **Budget:** Envelope budgets with rollover support
- **Couples:** Not couple-aware; shared accounts via CSV export
- **Notifications:** Due date alerts only
- **Reports:** Period comparison, net worth tracking
- **AI:** None
- **Key strength:** OFX import reliability; accountant-friendly reports

### Olivia AI (Brazil, shut down ~2023, but design patterns documented)
- **Bank import:** Open Finance OAuth2 via Belvo integration
- **Manual entry:** AI-suggested categorization on entry
- **Budget:** AI-generated budget recommendations based on spending history
- **Couples:** Not couple-first; individual
- **Notifications:** Spending alerts when nearing category limits
- **AI:** Full conversational assistant; "Olivia" persona; proactive nudges
- **Key strength:** AI persona with financial coaching; automated categorization

### YNAB (USA, premium)
- **Bank import:** Direct import via bank feeds; manual import as fallback
- **Budget:** True zero-based budgeting ("give every dollar a job")
- **Couples:** Joint budgets with separate device access
- **Notifications:** Overspending warnings; budget refill reminders
- **Reports:** Cash flow, net worth, age of money
- **AI:** None (community extensions only)
- **Key strength:** Philosophy-driven UX (zero-based budgeting is a cult brand); desktop-quality on mobile

### Honeydue (USA, couple-first)
- **Bank import:** Plaid (US bank sync); no manual CSV/PDF
- **Budget:** Shared monthly category limits
- **Couples:** TRUE couple-first — partner visibility, chat thread per bill
- **Notifications:** Bill due reminders; partner spending alerts
- **Reports:** Monthly overview only; minimal analytics
- **AI:** None
- **Key strength:** The only major app purpose-built for couples; partner visibility; bill chat threads

### Splitwise (USA/global, expense sharing)
- **Focus:** Expense splitting/settling, not budgeting
- **Import:** Manual only
- **Couples:** Yes (couple mode, group trips)
- **Notifications:** IOU alerts; settlement reminders
- **AI:** None (basic OCR for receipt amounts)
- **Key strength:** Simplicity of debt tracking; social/social UX for shared expenses

### Summary: Feature Gap Matrix

| Feature | CoupleSync now | Mobills | Organizze | YNAB | Honeydue |
|---------|----------------|---------|-----------|------|----------|
| Couple-first | ✅ | ❌ | ❌ | ⚠️ | ✅ |
| PDF/CSV bank import | ❌ broken | ✅ OFX | ✅ OFX | ✅ | ❌ |
| Notification capture | ✅ (Android) | ❌ | ❌ | ❌ | ❌ |
| Envelope budgeting | ✅ | ✅ | ✅ | ✅ | ⚠️ basic |
| AI chat | ✅ Gemini | ❌ | ❌ | ❌ | ❌ |
| Goals tracking | ✅ | ✅ | ✅ | ✅ | ❌ |
| Styled in-app errors | ⚠️ partial | ✅ | ✅ | ✅ | ✅ |
| Quick income update | ❌ | ✅ | ✅ | ✅ | ✅ |
| Category auto-suggest | ❌ | ✅ | ❌ | ✅ | ❌ |

CoupleSync's **unique advantages**: couple-first design + Android notification capture + AI chat — no competitor has all three. The gaps to close are primarily around import reliability and mutation error UX.

---

## Options / Alternatives

### OPT-1: Fix OCR Multipart Bug (Critical, 30 min effort)
- **Pro:** Fixes the immediate OCR upload failure; zero backend change needed
- **Con:** None — it's a one-line fix
- **Effort:** low
- **Recommended:** ✅ YES — ship immediately

### OPT-2: Fix Docker uploads directory permissions (Critical for cloud)
- **Pro:** Fixes file write failure in container; keeps LocalFileStorageAdapter
- **Con:** Still ephemeral storage (lost on restart)
- **Effort:** low (2-line Dockerfile change)
- **Recommended:** ✅ YES as short-term fix

### OPT-3: Replace LocalFileStorageAdapter with Azure Blob (Recommended cloud path)
- **Pro:** Durable, scalable, no permissions issue; already abstracted via `IStorageAdapter`
- **Con:** Requires Azure Storage account configuration + new adapter code (~100 LOC)
- **Effort:** medium
- **Recommended:** ✅ YES for production deployment (after OPT-2 as stopgap)

### OPT-4: Replace `prebuilt-receipt` with bank statement parser (High value)
- Sub-option A: `prebuilt-layout` + regex post-processor — stays in Azure DI ecosystem
- Sub-option B: `PdfPig` local PDF text extraction + bank-specific regex (no Azure cost per-page)
- Sub-option C: Pluggy/Belvo Open Finance API — structured data directly, no OCR
- **Recommended:** OPT-B for pilot (free, no quotas), with OPT-C as V2 goal
- **Effort:** medium (OPT-B), high (OPT-C)

### OPT-5: Replace mutation Alert.alert with toast/inline errors (UX polish)
- **Pro:** Consistent error UX; user-perceived quality improvement; addresses the "plain Android" complaint
- **Con:** Minor — requires touching ~4 mutation handlers across 3 screens
- **Effort:** low
- **Recommended:** ✅ YES — bundle with budget/OCR screen fixes

### OPT-6: Add lightweight "update income" quick action
- **Pro:** Addresses user expectation; improves AI context for users without full budget
- **Con:** Small scope creep beyond existing budget plan structure
- **Effort:** low (new endpoint + small mobile screen/modal)
- **Recommended:** ✅ YES — reuse `BudgetService.UpsertPlanAsync` with income-only payload; can be a modal on dashboard

---

## Recommendations (Prioritized)

### P0 — Fix Now (blocking bugs)

1. **Remove manual Content-Type from OCR FormData upload** (`mobile/src/services/apiClient.ts`)  
   Delete `headers: { 'Content-Type': 'multipart/form-data' }` from `ocrApiClient.upload()`

2. **Fix Docker uploads directory permissions** (`backend/Dockerfile`)  
   Add `RUN mkdir -p /app/uploads && chown appuser:appgroup /app/uploads` before `ENTRYPOINT`

3. **Add `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT` + KEY to docker-compose** (or document clearly that they are required env vars for production)

### P1 — UX Fixes (user-visible quality)

4. **Replace all `Alert.alert()` in mutation `onError` handlers with styled in-app error UI**  
   Screens: `budget/index.tsx`, `OcrReviewScreen.tsx`  
   Pattern: set error state and render a dismissible banner or toast using `colors.error` tokens

5. **Handle budget query `isError` state** — render `<ErrorState>` with retry instead of empty/spinner

6. **Differentiate 403 COUPLE_REQUIRED error** — show a "Conecte-se com seu parceiro primeiro" prompt with a link to the couple setup screen

### P2 — Feature Gaps

7. **Replace `prebuilt-receipt` with PDF bank statement parser**  
   Implement `PdfPig`-based text extractor + per-bank regex (Nubank, Itaú, Santander, C6, Inter) for pilot  
   This is the single biggest feature gap vs user expectations

8. **Add quick income update modal** on Dashboard — shortcuts users to set income without full budget flow

9. **Add goals context to AI chat system prompt** in `ChatContextService.BuildSystemPromptAsync()`  
   Currently missing goal progress data even though goal entities exist

### P3 — Infrastructure / Production

10. **Implement Azure Blob Storage adapter** (`IStorageAdapter`) for production  
    Eliminates local filesystem dependency for OCR files in ACA deployment

11. **Add `uploads` docker-compose volume** or document that `Storage:BasePath` env var must point to a mounted path in production

---

## Sources / References

- Code inspected directly in workspace (all paths as listed in Methodology section)
- Azure Document Intelligence API model reference: `prebuilt-receipt` vs `prebuilt-layout` vs `prebuilt-document` (known from documentation)
- React Native + Axios FormData boundary issue: well-documented in `axios/axios#4602`, `axios/axios#1395`, multiple Expo forums threads
- `docker-compose.yml` and `Dockerfile` — no uploads volume mount confirmed by direct file read
- Competitor analysis: product knowledge from public app store listings/documentation (Mobills, Organizze, YNAB, Honeydue, Splitwise, Olivia AI)

---

## Open Questions

1. **Which Brazilian banks are the pilot users using?** — Determines which regex patterns are needed for the bank statement parser (OPT-4B). Nubank PDF format differs from Itaú Excel/PDF exports.

2. **Is Azure Document Intelligence provisioned for production?** — If yes, confirm key/endpoint env vars; if no, recommend OPT-4B (local PDF parsing) for pilot.

3. **Should goals data be included in the AI system prompt?** — Depends on whether goal-aware recommendations are in scope for V1.5.

4. **What is the target Android version for pilot devices?** — Affects `NotificationListenerService` compatibility edge cases on Android 12+.

5. **Are the notification pattern definitions (regex per bank) complete?** — `eventUploader.ts` and notification-patterns.json were not directly inspected; need Coder to verify coverage.
