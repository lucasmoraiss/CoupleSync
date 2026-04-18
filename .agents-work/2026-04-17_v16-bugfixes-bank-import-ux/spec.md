# CoupleSync V1.6 — Bugfixes, Bank Statement Import, UX Polish, Feature Enhancements

**Session:** `2026-04-17_v16-bugfixes-bank-import-ux`  
**Date:** 2026-04-17  
**Previous session:** `2026-04-17_v15-cloud-budgets-ocr`

---

## Problem Statement

V1.5 shipped core infrastructure (cloud, budgets, OCR, AI chat) but left three critical production blockers and several UX rough edges:

1. **OCR upload always fails** — React Native sets multipart/form-data without a boundary because the Content-Type header is manually overridden, causing ASP.NET Core to receive `file == null`.
2. **Docker container cannot write uploads** — The non-root `appuser` lacks write permission on `/app/uploads/` because the directory is created as root before the user switch.
3. **Bank statement PDFs are never parsed** — The OCR pipeline uses `prebuilt-receipt` (Azure Document Intelligence), which returns zero fields for tabular bank exports. No Brazilian PDF bank statement is supported.

Beyond blockers, V1.5 shipped all mutation errors as plain native Android `Alert.alert()` dialogs — visually out-of-brand and offering no recovery guidance. `COUPLE_REQUIRED` (403) is indistinguishable from a 500 server crash. The budget screen does not show an `ErrorState` on query failure.

High-value features deferred from V1.5 that are now ready for implementation: quick income update, AI-powered transaction categorization, goals data in AI chat context, and visual spending reports.

---

## Goals

- Fix all three P0 production blockers: OCR multipart boundary, Docker uploads permissions, and bank PDF model mismatch.
- Replace Azure Document Intelligence (paid, wrong model) with a free, local, .NET-native PDF text extraction + bank-specific regex parser covering Inter, Banco do Brasil, Mercantil, Nubank, Caixa, Itaú, Santander, and Swile (nice-to-have).
- Replace all `Alert.alert()` mutation error handlers with a styled in-app toast component consistent with app theme.
- Surface `COUPLE_REQUIRED` (403) with a dedicated actionable message pointing the user to couple setup.
- Add budget query `ErrorState` rendering on failure.
- Implement Quick Income Update modal on Dashboard.
- Implement AI auto-categorization for transactions imported via OCR or manual entry.
- Inject goals context (progress, amounts, deadlines) into the AI chat system prompt.
- Add a Visual Reports screen with bar/pie charts for spending by category and trends over time.
- Produce an App Usage Guide documenting the product value proposition and comparison with market alternatives.

---

## Non-Goals

- iOS support
- Microservices decomposition
- Open Finance OAuth2 integration (deferred to V2)
- Streaming AI responses
- OFX/CSV file import (separate future feature)
- Advanced ML-based categorization model training
- Push notification reminders for budget limits (separate future feature)

---

## User Stories

**US-001 (P0 — OCR upload works):** As a pilot user I want to photograph or upload a bank statement PDF so that the OCR service receives the file and returns extracted transactions — without it failing silently due to a missing multipart boundary.

**US-002 (P0 — Docker OCR works in production):** As a deployed user I want OCR uploads to be written to disk inside the Docker container so that the background processing job can read and process the file — not fail with a permission error.

**US-003 (P0 — Bank statement parsing):** As a pilot user I want to import my bank statement PDF from Inter, Nubank, Itaú, Santander, Banco do Brasil, Mercantil, or Caixa so that the system extracts my individual transactions automatically, without requiring Azure Document Intelligence.

**US-004 (P1 — Toast errors):** As a user I want mutation errors (budget save, OCR confirm, goal mutations) to appear as styled in-app notifications rather than plain Android system dialogs so that the error feels consistent with the app's design language.

**US-005 (P1 — COUPLE_REQUIRED UX):** As a new user who has not yet connected with my partner I want to see a clear message "Conecte-se com seu parceiro primeiro" with a navigation link to the couple setup screen, instead of a generic error.

**US-006 (P1 — Budget error state):** As a user whose budget query fails due to a network or server error I want to see a retry-capable `ErrorState` component instead of an empty/loading screen.

**US-007 (P3 — Quick income update):** As a user I want to update my monthly income directly from the Dashboard using a compact modal, without having to navigate to the full budget screen.

**US-008 (P3 — AI auto-categorization):** As a user importing transactions via OCR or adding manually I want the app to suggest a category for each transaction automatically using Gemini so that I spend less time on manual categorization.

**US-009 (P3 — Goals in AI chat):** As a user chatting with the AI financial assistant I want responses to reflect my active financial goals (progress, target amounts, deadlines) so that the AI gives more relevant advice on my savings trajectory.

**US-010 (P3 — Visual reports):** As a user I want to see a Reports screen with bar charts showing monthly spending by category and a pie chart of the current-month category breakdown so that I can identify overspending patterns at a glance.

**US-011 (P4 — App usage guide):** As a potential user or pilot tester I want to read a guide explaining what CoupleSync does, what problems it solves, and how it compares to Mobills, Organizze, YNAB, Splitwise, and Honeydue.

---

## Functional Requirements

### FR-001 — OCR Multipart Fix (P0)
Remove the explicit `Content-Type: multipart/form-data` header override from `ocrApiClient.upload()` in `mobile/src/services/apiClient.ts`. React Native's XHR layer must auto-set `multipart/form-data; boundary=<uuid>`.

### FR-002 — Docker Uploads Permissions (P0)
Add `RUN mkdir -p /app/uploads && chown appuser:appgroup /app/uploads` to `backend/Dockerfile` **after** the group/user creation statements and **before** `USER appuser`.

### FR-003 — Local PDF Bank Statement Parser (P0/P2)
- Add NuGet package `PdfPig` (Apache 2.0) to `CoupleSync.Infrastructure`.
- Create `ILocalPdfParser` interface with `Parse(Stream pdfStream): ParsedBankStatement`.
- Implement `LocalPdfParserProvider` that:
  1. Extracts full text from PDF using PdfPig (text-based PDFs).
  2. Falls back to Tesseract OCR for image-based / scanned PDFs when text extraction returns < 50 characters.
  3. Auto-detects the bank from header/footer keywords in the extracted text.
  4. Dispatches to the matched bank-specific regex parser.
  5. Returns a list of `ParsedTransaction` (date, description, amount, type: credit/debit).

### FR-004 — Bank-Specific Parsers (P2)
Implement dedicated regex-based parsers (one class each) for:
- Banco Inter
- Banco do Brasil
- Mercantil do Brasil
- Nubank
- Caixa Econômica Federal
- Itaú Unibanco
- Santander
- Swile (nice-to-have, lower priority)

Each parser must implement `IBankStatementParser` and return `IReadOnlyList<ParsedTransaction>`.

### FR-005 — Replace Azure DI Adapter with Local Parser (P2)
- `IOcrProvider` registration in `DependencyInjection.cs` switches from `AzureDocumentIntelligenceAdapter` to `LocalPdfParserProvider` when `USE_LOCAL_PDF_PARSER=true` env var (default `true`).
- `AzureDocumentIntelligenceAdapter` is retained but disabled by default; not deleted (allows rollback).
- `OcrProcessingService.ParseCandidates()` is updated to accept the `ParsedBankStatement` shape from the local parser.

### FR-006 — Styled Toast Component (P1)
- Create `mobile/src/components/Toast.tsx` (or `Snackbar.tsx`) using app theme colors (error: red-400, success: green-500, info: blue-400).
- Expose a `useToast()` hook backed by a context provider added to `app/_layout.tsx`.
- Supports: `toast.error(message)`, `toast.success(message)`, `toast.info(message)`.
- Auto-dismisses after 4 seconds; can be dismissed by tap.
- Accessible (role="alert", accessibilityLiveRegion).

### FR-007 — Replace Alert.alert() with Toast (P1)
Replace `Alert.alert()` calls in:
- `mobile/app/(main)/budget/index.tsx` — `saveMutation.onError`
- `mobile/src/modules/ocr/screens/OcrReviewScreen.tsx` — `confirmMutation.onError`  
- Any other `onError` handlers found to use `Alert.alert()` during implementation

### FR-008 — COUPLE_REQUIRED 403 Differentiation (P1)
In the 403 response interceptor (or in individual `onError` handlers), detect `error.response?.data?.code === 'COUPLE_REQUIRED'` and display: _"Conecte-se com seu parceiro primeiro"_ with a button navigating to the couple setup screen.

### FR-009 — Budget Query ErrorState (P1)
In `mobile/app/(main)/budget/index.tsx`, render `<ErrorState ... onRetry={() => refetch()} />` when `isError` is `true` from the budget query.

### FR-010 — Quick Income Update Modal (P3)
- Add a `QuickIncomeModal` component to the Dashboard screen.
- Triggered by tapping an "Atualizar renda" button/chip near the income display.
- Sends a `PATCH /api/v1/budget/income` request (new lightweight endpoint) with `{ grossIncome: number }`.
- Backend endpoint updates only the `GrossIncome` field of the active budget plan for the authenticated couple.

### FR-011 — AI Auto-Categorization (P3)
- Add `ICategoryClassifier` interface in `CoupleSync.Application`.
- Implement `GeminiCategoryClassifier` in `CoupleSync.Infrastructure` that calls Gemini with a transaction description and returns a suggested `CategoryId` from the couple's budget categories.
- Wire into `OcrProcessingService` (post-parse, before returning candidates) and `CreateTransactionCommandHandler` (manual entry, as a suggestion — not forced).
- Prompts must be structured to avoid injection: transaction text must be passed as a clearly delimited user field, not interpolated directly into the instruction text.

### FR-012 — Goals Context in AI Chat (P3)
- In `ChatContextService.BuildSystemPromptAsync()`, query `IGoalRepository.GetByCouple(coupleId)` and inject a goals summary section into the system prompt.
- Format: goal name, target amount, current progress amount, progress %, deadline.
- Only goals with `Status == Active` are included.

### FR-013 — Visual Reports Screen (P3)
- Add a new `Reports` tab in `app/(main)/`.
- Bar chart: Monthly totals per category for the last 6 months.
- Pie chart: Current-month spending distribution by category.
- Use `react-native-gifted-charts` or `victory-native` (free, MIT licensed).
- Data fetched from new `GET /api/v1/reports/spending?months=6` endpoint.
- Backend endpoint is couple-scoped; returns `{ month: string, categories: { name, total }[] }[]`.

### FR-014 — App Usage Guide (P4)
Create `docs/app-usage-guide.md` covering:
- What CoupleSync is and what problem it solves for couples
- Key features and how to use each one
- Comparison table vs Mobills, Organizze, YNAB, Splitwise, Honeydue
- CoupleSync's unique value proposition (couple-first, passive notification capture, AI chat, free tier)

---

## Non-Functional Requirements

**NFR-001 (Security):** PDF file uploads must validate MIME type (`application/pdf`) and file size (max 20 MB) before processing. Reject non-PDF files with 400.

**NFR-002 (Security):** PdfPig text extraction must operate on a copy of the stream in memory; no temp files written as the running OS user outside `/app/uploads/{coupleId}/`. 

**NFR-003 (Security):** Gemini prompts for auto-categorization must use structured prompt templates with clearly delimited user-controlled content to prevent prompt injection.

**NFR-004 (Security):** `LocalPdfParserProvider` must not expose raw bank statement content in logs; only log transaction count and detected bank name.

**NFR-005 (Performance):** PDF text extraction + parsing must complete within 5 seconds for a standard monthly bank statement (≤ 100 transactions, ≤ 5 MB).

**NFR-006 (Performance):** Reports endpoint must respond within 2 seconds for a couple with 12 months of data (≤ 5,000 transactions).

**NFR-007 (Usability):** Toast component must not obscure primary action buttons; render above the bottom tab bar with appropriate z-index.

**NFR-008 (Usability):** Quick Income Update modal must be dismissible by tapping outside or pressing the Android back button.

**NFR-009 (Cost):** All new cloud or library dependencies must remain on free tiers. PdfPig and Tesseract (via Tesseract.NET) are Apache 2.0. No new Azure services.

**NFR-010 (Reliability):** If the local PDF parser fails to detect the bank format, return a clear `422 BANK_FORMAT_UNKNOWN` error with a user-facing message listing the supported banks.

---

## Edge Cases

**EC-001:** PDF is password-protected → PdfPig throws; catch and return `400 PDF_ENCRYPTED` with user guidance to export an unencrypted PDF.

**EC-002:** PDF is scanned (image-only) and Tesseract is not installed/available → Return `422 OCR_UNAVAILABLE` with a message to upload a digital export rather than a scan.

**EC-003:** Bank is partially detected (keyword match) but regex finds 0 transactions → Return `422 NO_TRANSACTIONS_FOUND`; do not return an empty import silently.

**EC-004:** PDF text is detected as a different bank than the user manually selects (if a manual override UI is provided) → User selection overrides auto-detection.

**EC-005:** `COUPLE_REQUIRED` 403 fires during background query refresh (not user-initiated mutation) → Show a non-intrusive toast, do not navigate; user may be in the process of connecting.

**EC-006:** Quick Income update modal is submitted while a pre-existing background budget save is in-flight → Debounce / disable submit button; prevent double-write.

**EC-007:** Gemini auto-categorization call times out or returns an error → OCR/transaction import still completes with `category = null`; auto-categorization failure is non-blocking.

**EC-008:** Gemini category suggestion returns a `CategoryId` that does not exist in the couple's current budget plan → Silently ignore and set `category = null` instead of throwing a 500.

**EC-009:** Reports endpoint called when couple has no transaction history → Return empty arrays, not 404 or 500.

**EC-010:** PDF upload exceeds 20 MB size limit → Return `400 FILE_TOO_LARGE` before attempting parsing.

**EC-011:** Toast shown during navigation transition (screen animating out) → Toast must persist across navigation until dismissed or auto-expired.

**EC-012:** Multiple concurrent OCR jobs for the same couple → `ImportJobService` must handle duplicate-submission gracefully (idempotency key or per-couple job queue check).

---

## Assumptions

- V1.5 codebase is fully deployed and test baseline is ≥ 316 tests green.
- PdfPig successfully extracts text from the major Brazilian banks' standard PDF export formats (text-based, not scanned). Tesseract is available as an optional fallback but not required for the primary flow.
- Existing `IOcrProvider` abstraction in `DependencyInjection.cs` allows swapping to `LocalPdfParserProvider` without changing the background job or controller.
- `ICategoryRepository` / category lookup is already injectable in Application layer handlers.
- `IGoalRepository.GetByCouple(coupleId)` already exists or can be extracted from existing Goal queries.
- `react-native-gifted-charts` or `victory-native` is compatible with current Expo SDK version.
- The Reports bar/pie chart data can be derived from existing `transactions` table with a GROUP BY query; no new tables required.
- `GET /api/v1/reports/spending` is couple-scoped via the authenticated user's couple, consistent with all existing endpoints.
- Swile parser is a nice-to-have; shipping without it does not block the P2 milestone.

---

## Definition of Done

- [ ] `dotnet build` clean (0 errors, 0 warnings treated as errors)
- [ ] `dotnet test` green — all existing 316 tests pass; net-new tests for P0/P1/P2 added (target ≥ 335 total)
- [ ] `npx tsc --noEmit` clean in `mobile/`
- [ ] OCR upload succeeds end-to-end with a real bank statement PDF in local dev Docker environment
- [ ] At least one bank parser (Inter or Nubank) returns correctly structured transactions for a sample PDF
- [ ] All `Alert.alert()` mutation error calls replaced — confirmed by `grep -r "Alert.alert" mobile/app/` returning 0 results
- [ ] Toast component renders and auto-dismisses on a physical Android device
- [ ] Quick Income Update modal updates `GrossIncome` via API and Dashboard reflects change without full reload
- [ ] Goals data appears in AI chat system prompt (verified via backend log or test)
- [ ] Reports screen renders charts with real couple transaction data
- [ ] App Usage Guide document is present at `docs/app-usage-guide.md`
- [ ] `status.json` updated to `DONE` with `last_update` timestamp
- [ ] All acceptance criteria AC-601 through AC-616 in `acceptance.json` verified

---

## Acceptance Criteria

See `acceptance.json` for machine-readable definitions. Summary:

| ID | Feature | Verification |
|----|---------|-------------|
| AC-601 | OCR multipart upload succeeds (file received by backend) | Integration test + manual |
| AC-602 | Docker uploads directory is writable by appuser | Docker build + manual |
| AC-603 | PdfPig extracts text from a text-based bank statement PDF | Unit test |
| AC-604 | Bank auto-detection identifies correct bank from sample PDFs | Unit test (≥ 5 banks) |
| AC-605 | Bank parsers return correct transaction list from sample PDFs | Unit test (≥ 3 banks with fixtures) |
| AC-606 | `USE_LOCAL_PDF_PARSER=true` routes to LocalPdfParserProvider | Integration test |
| AC-607 | `Alert.alert()` is absent from mutation error paths | grep / static check |
| AC-608 | Toast renders with correct error styling on budget save failure | Manual on-device |
| AC-609 | COUPLE_REQUIRED 403 shows "Conecte-se com seu parceiro" + nav link | Integration test + manual |
| AC-610 | Budget query failure renders ErrorState with retry button | Unit test (mock query error) |
| AC-611 | Quick Income modal updates GrossIncome and Dashboard refreshes | Integration test + manual |
| AC-612 | AI auto-categorization assigns category to OCR transactions | Integration test (mocked Gemini) |
| AC-613 | Goals data present in AI system prompt when active goals exist | Unit test on ChatContextService |
| AC-614 | Reports screen renders bar and pie charts with correct data | Manual + snapshot |
| AC-615 | Reports endpoint returns correct aggregated data, couple-scoped | Integration test |
| AC-616 | App Usage Guide exists at docs/app-usage-guide.md | File existence check |
