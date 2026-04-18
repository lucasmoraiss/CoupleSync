# CoupleSync V1.6 Architecture

> Extends V1.5 architecture (`.agents-work/2026-04-17_v15-cloud-budgets-ocr/architecture.md`).
> All prior design decisions (modular monolith, couple-level isolation, notification capture, FCM, cloud deployment) remain unchanged.
> V1.6 fixes three P0 production blockers, replaces Azure Document Intelligence with a free local PDF parser, adds toast-based error UX, quick income update, AI auto-categorization, goals context in AI chat, and a visual reports screen.

---

## Overview

V1.6 is a focused hardening and feature-enrichment release. The three P0 blockers (multipart boundary bug, Docker permissions, wrong OCR model) are resolved before any feature work. The OCR pipeline is rearchitected around a local PdfPig-based parser with a Strategy pattern for bank-specific regex parsers, replacing Azure Document Intelligence for PDF imports while preserving the Azure/Google Vision path for image uploads. Error UX is unified behind a custom Toast component injected at the root layout. Three P3 features are wired into existing services: quick income PATCH endpoint, AI auto-categorization alongside the OCR confirm flow, and goals context injected into the Gemini system prompt. A new Reports module adds spending aggregation without new database tables.

---

## Modules and Responsibilities

### [Unchanged] Auth, Couple, NotificationCapture, Transaction, Goal, CashFlow, Notification, Budget, OCR Import (flow logic), AI Chat
All V1 and V1.5 modules carry forward without breaking changes. V1.6 modifies selected services and adds new sub-components within existing module folders.

---

### PDF Bank Parser Sub-system (new — under OCR Import module)
- **Backend folder**: `backend/src/CoupleSync.Infrastructure/Integrations/LocalPdfParser/`
- **Responsibility**: Extract text from PDF bank statements using PdfPig, detect the issuing bank by keyword matching, dispatch to a bank-specific regex parser, and return a normalized transaction list serialized as JSON consumed by the existing `OcrProcessingService`.
- **Key classes**:
  - `LocalPdfParserProvider` — implements `IOcrProvider`; orchestrates PdfPig extraction → bank detection → parser dispatch.
  - `BankDetector` — iterates registered `IBankStatementParser` implementations and returns the first whose `CanParse()` returns true.
  - `InterBankParser`, `NubankParser`, `BancoBrasilParser`, `MercantilParser`, `CaixaParser`, `ItauParser`, `SantanderParser` — each implements `IBankStatementParser`.
- **Domain additions** (`CoupleSync.Domain`):
  - `IBankStatementParser` interface (in `Interfaces/`).
  - `ParsedTransaction` value object (in `ValueObjects/`).
  - `ParsedBankStatement` value object (in `ValueObjects/`).
- **Feature flag**: `USE_LOCAL_PDF_PARSER` env var (default `true`). When `false`, `AzureDocumentIntelligenceAdapter` is registered for `IOcrProvider` (rollback path preserved).

---

### Reports Module (new)
- **Backend folder**: `backend/src/CoupleSync.Application/Reports/`
- **Responsibility**: Aggregate transaction history by category and month for visual reporting. No new database tables — pure read-side aggregation of the existing `transactions` table.
- **Key classes**:
  - `ReportsService` — `GetSpendingAsync(coupleId, months, ct)`.
  - `ReportsSpendingDto`, `MonthlySpendingDto` — response shapes.
  - `IReportsRepository` interface (in `CoupleSync.Application/Common/Interfaces/`).
  - `ReportsRepository` implementation (in `CoupleSync.Infrastructure/Persistence/`).
- **Controller**: `ReportsController` at `api/v1/reports` (new file in `CoupleSync.Api/Controllers/`).
- **Mobile screen**: `mobile/app/(main)/reports/index.tsx` — new Reports tab using `react-native-gifted-charts`.

---

### Toast/Snackbar System (new — mobile only)
- **Mobile folder**: `mobile/src/components/Toast/`
- **Responsibility**: Provide a themeable, auto-dismissing, accessible in-app notification replacing all `Alert.alert()` mutation error handlers.
- **Key files**:
  - `ToastProvider.tsx` — React context provider wrapping `ToastOverlay` component; manages queue and timers.
  - `Toast.tsx` — visual component using theme colors (`error`: red-400, `success`: green-500, `info`: blue-400).
  - `useToast.ts` — hook exposing `toast.error()`, `toast.success()`, `toast.info()`.
- **Placement**: Provider added to `app/_layout.tsx` (root layout, wraps everything including tab navigator).

---

### Quick Income Update (new endpoint and modal)
- **Backend**: New `PATCH /api/v1/budget/income` in `BudgetController`. Thin: delegates to `BudgetService.UpdateIncomeAsync()`.
- **Mobile**: `QuickIncomeModal` component in `mobile/src/components/` rendered as a bottom sheet on the Dashboard screen.

---

### AI Auto-Categorization (new — under OCR Import + Transaction modules)
- **Application layer**: New `ICategoryClassifier` interface in `CoupleSync.Application/Common/Interfaces/`.
- **Infrastructure layer**: `GeminiCategoryClassifier` in `CoupleSync.Infrastructure/Integrations/Gemini/`.
- **Integration point**: `OcrProcessingService.ParseAndDeduplicateAsync()` — after fingerprinting, classifies each candidate description; failure is silenced, `suggestedCategoryId` remains `null`.
- **Manual entry**: `TransactionService` (or its command handler) calls `ICategoryClassifier.SuggestCategoryAsync()` and returns suggestion in response DTO; does not force the category.

---

### Goals Context in AI Chat (extension of existing ChatContextService)
- `ChatContextService.BuildSystemPromptAsync()` gains `IGoalRepository` dependency; appends an active-goals section to the Gemini system prompt.

---

## Data Flow

### P0 Fix: OCR Multipart Upload
```
Mobile FormData (no manual Content-Type override)
  → RN XHR auto-sets: multipart/form-data; boundary=<uuid>
  → POST /api/v1/ocr/upload
  → OcrController receives IFormFile (non-null)
  → ImportJobService stores file to /app/uploads/{coupleId}/{jobId}.pdf [appuser-owned dir]
  → 202 Accepted + {upload_id}
```

### PDF Bank Statement Parse Flow (replaces Azure DI path for PDFs)
```
OcrBackgroundJob polls import_jobs WHERE status=Pending
  → job.FileMimeType == "application/pdf"
  → IOcrProvider.AnalyzeAsync(storagePath, mimeType, ct)
      → LocalPdfParserProvider:
          1. Load stream from IStorageAdapter
          2. PdfPig: extract full text
          3. If text.Length < 50 → throw OcrUnavailableException (EC-002)
          4. BankDetector.Detect(text):
               → iterate IBankStatementParser.CanParse(text) in registration order
               → first match wins → bank parser selected
               → if no match → throw BankFormatUnknownException (EC-003)
          5. parser.Parse(text) → IReadOnlyList<ParsedTransaction>
          6. Build ParsedBankStatement { bank, transactions }
          7. Serialize to JSON with discriminator: { "provider": "local-pdf", ... }
          8. Return JSON string
  → OcrProcessingService.ParseCandidates(json):
      → detect discriminator "provider"
      → "local-pdf" → ParseLocalPdfCandidates(json)
      → absent       → existing Azure ParseCandidates(json) (unchanged)
  → deduplicate → ICategoryClassifier for each candidate (failure-safe)
  → job.MarkReady(candidatesJson)
```

### Quick Income Flow
```
Mobile Dashboard: tap "Atualizar renda" → QuickIncomeModal(open)
  → user enters income → tap Save
  → PATCH /api/v1/budget/income { grossIncome, currency }
  → BudgetService.UpdateIncomeAsync():
      → GetByMonthAsync(coupleId, currentMonth)
      → if null → UpsertPlanAsync (create new plan, 0 allocations)
      → if exists → plan.Update(grossIncome), SaveChanges
  → 200 OK { plan_id, gross_income, month }
  → React Query invalidates "dashboard" + "budget" queries
  → Dashboard income widget re-renders
```

### Reports Data Flow
```
GET /api/v1/reports/spending?months=6
  → ReportsController → ReportsService.GetSpendingAsync(coupleId, months=6)
  → IReportsRepository.GetMonthlySpendingByCategoryAsync(coupleId, since, ct)
      → SQL: GROUP BY DATE_TRUNC('month', date), category over last N months
  → ReportsSpendingDto serialized
  → 200 OK

Mobile ReportsScreen:
  → useQuery("reports-spending") fetches data
  → BarChart (last 6 months per-category totals)
  → PieChart (current-month category breakdown)
  → react-native-gifted-charts renders
```

### AI Auto-Categorization Flow
```
OcrProcessingService.ParseAndDeduplicateAsync():
  [after dedup fingerprinting]
  → availableCategories = couple's budget allocation category names (or default list if no plan)
  → for each candidate:
      try:
        suggestedCategoryId = await ICategoryClassifier.SuggestCategoryAsync(
            description: candidate.Description,
            categories: availableCategories, ct)
      catch: suggestedCategoryId = null  (never blocks import)
  → OcrCandidate.SuggestedCategoryId populated

OcrReviewScreen:
  → shows suggested category as a pre-selected chip per transaction
  → user can override before confirming
```

### Goals in AI Chat
```
POST /api/v1/chat { message, history }
  → ChatContextService.BuildSystemPromptAsync(coupleId):
      1. Existing: budget + 30-day transactions
      2. NEW: IGoalRepository.GetByCoupleAsync(coupleId)
            → filter Status == Active
            → append: goal name, target, progress, %, deadline
  → System prompt injected into Gemini request
```

---

## Interfaces / Contracts

### New Backend Interfaces

```csharp
// CoupleSync.Domain/Interfaces/IBankStatementParser.cs
public interface IBankStatementParser
{
    string BankName { get; }
    bool CanParse(string extractedText);
    IReadOnlyList<ParsedTransaction> Parse(string extractedText);
}

// CoupleSync.Application/Common/Interfaces/ICategoryClassifier.cs
public interface ICategoryClassifier
{
    Task<string?> SuggestCategoryAsync(
        string description,
        IReadOnlyList<string> availableCategories,
        CancellationToken ct);
}

// CoupleSync.Application/Common/Interfaces/IReportsRepository.cs
public interface IReportsRepository
{
    Task<IReadOnlyList<MonthlySpendingDto>> GetMonthlySpendingByCategoryAsync(
        Guid coupleId,
        DateOnly since,
        CancellationToken ct);
}
```

### New API Endpoints (V1.6 additions)

| Method | Path | Description | AC |
|--------|------|-------------|-----|
| `PATCH` | `/api/v1/budget/income` | Update gross income for current month's plan | AC-611 |
| `GET` | `/api/v1/reports/spending?months=N` | Monthly spending by category | AC-615 |

#### PATCH /api/v1/budget/income
**Body:**
```json
{ "grossIncome": 10000.00, "currency": "BRL" }
```
**Response 200:**
```json
{ "planId": "...", "month": "2026-04", "grossIncome": 10000.00, "currency": "BRL" }
```
**Behavior**: If no plan exists for the current month, one is auto-created. `[RequireCouple]` + `[Authorize]` filters apply.

#### GET /api/v1/reports/spending?months=N
**Response 200:**
```json
{
  "months": [
    {
      "month": "2026-04",
      "categories": [
        { "name": "Alimentação", "total": 850.00 },
        { "name": "Transporte", "total": 320.00 }
      ]
    }
  ]
}
```

### Updated OcrCandidate Shape (V1.6)
```json
{
  "index": 0,
  "date": "2026-04-10",
  "description": "Supermercado BH Ltda",
  "amount": 143.50,
  "currency": "BRL",
  "confidence": 1.0,
  "duplicate_suspected": false,
  "suggested_category_id": "Alimentação"
}
```

### Mobile Toast Hook API
```typescript
const { toast } = useToast();
toast.error("Erro ao salvar orçamento. Tente novamente.");
toast.success("Orçamento salvo com sucesso.");
toast.info("Processando extrato...");
```

---

## Directory Layout Proposal

### Backend additions
```
backend/src/
  CoupleSync.Domain/
    Interfaces/
      IBankStatementParser.cs           (new)
    ValueObjects/
      ParsedTransaction.cs              (new)
      ParsedBankStatement.cs            (new)

  CoupleSync.Application/
    Common/Interfaces/
      ICategoryClassifier.cs            (new)
      IReportsRepository.cs             (new)
    Reports/
      ReportsService.cs                 (new)
      ReportsSpendingDto.cs             (new)
    OcrImport/
      OcrProcessingService.cs           (modified — add local-pdf parse path + AI categorization)
      OcrCandidate.cs                   (modified — add SuggestedCategoryId)
    Budget/
      BudgetService.cs                  (modified — add UpdateIncomeAsync)
    AiChat/
      ChatContextService.cs             (modified — add IGoalRepository, goals section)

  CoupleSync.Infrastructure/
    Integrations/
      LocalPdfParser/
        LocalPdfParserProvider.cs       (new — implements IOcrProvider)
        BankDetector.cs                 (new)
        Parsers/
          InterBankParser.cs            (new)
          NubankParser.cs               (new)
          BancoBrasilParser.cs          (new)
          MercantilParser.cs            (new)
          CaixaParser.cs                (new)
          ItauParser.cs                 (new)
          SantanderParser.cs            (new)
      Gemini/
        GeminiCategoryClassifier.cs     (new — implements ICategoryClassifier)
    Persistence/
      ReportsRepository.cs              (new)
    DependencyInjection.cs              (modified — feature flag + new registrations)

  CoupleSync.Api/
    Controllers/
      BudgetController.cs               (modified — add PATCH /income)
      ReportsController.cs              (new)
    Contracts/
      Reports/
        ReportsSpendingResponse.cs      (new)
      Budget/
        UpdateIncomeRequest.cs          (new)
        UpdateIncomeResponse.cs         (new)
```

### Mobile additions
```
mobile/
  app/
    (main)/
      reports/
        index.tsx                       (new — Reports tab screen)
      _layout.tsx                       (modified — add Reports tab + ToastProvider)
      index.tsx                         (modified — QuickIncomeModal trigger)
    _layout.tsx                         (modified — add ToastProvider at root)
  src/
    components/
      Toast/
        ToastProvider.tsx               (new)
        Toast.tsx                       (new)
        useToast.ts                     (new)
      QuickIncomeModal.tsx              (new)
    services/
      apiClient.ts                      (modified — remove Content-Type override on upload)
      reportsApiClient.ts               (new — or added to existing apiClient modules)
    modules/
      reports/                          (new)
        screens/
          ReportsScreen.tsx             (new)
        hooks/
          useReportsData.ts             (new)
```

---

## Error Handling Strategy

| Error condition | Backend response | Mobile handling |
|---|---|---|
| PDF encrypted | `400 PDF_ENCRYPTED` | `toast.error("PDF protegido por senha. Exporte sem senha.")` |
| Image-only PDF, no OCR | `422 OCR_UNAVAILABLE` | `toast.error("PDF escaneado não suportado. Use o extrato digital.")` |
| Bank not recognized | `422 BANK_FORMAT_UNKNOWN` | `toast.error("Banco não reconhecido. Bancos suportados: Inter, Nubank, Itaú, Santander, BB, Mercantil, Caixa.")` |
| No transactions parsed | `422 NO_TRANSACTIONS_FOUND` | `toast.error("Nenhuma transação encontrada neste extrato.")` |
| 403 COUPLE_REQUIRED | `403 COUPLE_REQUIRED` | Dedicated message + nav link to couple setup screen |
| Gemini categorization fail | Silenced; `suggestedCategoryId = null` | No feedback; import proceeds normally |
| Gemini chat 429 | `429` | `toast.info("Assistente ocupado. Tente novamente em breve.")` |
| Budget query error | `isError=true` on query | `<ErrorState onRetry={refetch} />` rendered |
| Mutation errors (general) | Any 4xx/5xx | `toast.error(message)` — never `Alert.alert()` |

---

## Configuration Strategy

### New environment variables (V1.6)

| Variable | Default | Purpose |
|---|---|---|
| `USE_LOCAL_PDF_PARSER` | `true` | Routes `IOcrProvider` to `LocalPdfParserProvider` when `true`; falls back to `AzureDocumentIntelligenceAdapter` when `false` |
| `AI_CATEGORIZATION_ENABLED` | `true` | Enables `GeminiCategoryClassifier`; when `false`, `ICategoryClassifier` resolves to a no-op implementation |

All other secrets remain in the Azure Container Apps secret store defined in V1.5. No new paid services.

### DependencyInjection.cs changes
```csharp
// OCR provider selection
var useLocalPdf = configuration["USE_LOCAL_PDF_PARSER"] != "false";
if (useLocalPdf)
{
    // Register bank parsers (order matters for BankDetector priority)
    services.AddScoped<IBankStatementParser, InterBankParser>();
    services.AddScoped<IBankStatementParser, NubankParser>();
    services.AddScoped<IBankStatementParser, ItauParser>();
    services.AddScoped<IBankStatementParser, SantanderParser>();
    services.AddScoped<IBankStatementParser, BancoBrasilParser>();
    services.AddScoped<IBankStatementParser, CaixaParser>();
    services.AddScoped<IBankStatementParser, MercantilParser>();
    services.AddScoped<BankDetector>();
    services.AddScoped<IOcrProvider, LocalPdfParserProvider>();
}
else
{
    services.AddHttpClient("AzureDocumentIntelligence", c => c.Timeout = TimeSpan.FromSeconds(15));
    services.AddScoped<IOcrProvider, AzureDocumentIntelligenceAdapter>();
}

// AI categorization
var aiCategorizationEnabled = configuration["AI_CATEGORIZATION_ENABLED"] != "false";
if (aiCategorizationEnabled)
    services.AddScoped<ICategoryClassifier, GeminiCategoryClassifier>();
else
    services.AddScoped<ICategoryClassifier, NoOpCategoryClassifier>();
```

---

## Security Considerations

- **PDF parsing in memory**: `LocalPdfParserProvider` processes the PDF stream in memory via PdfPig — no temp files are written to disk outside the pre-authorized uploads directory.
- **No raw statement content in logs**: Log only `{ bank: string, transactionCount: int }` after parsing; never log transaction descriptions, amounts, or raw extracted text.
- **Gemini prompt injection**: `GeminiCategoryClassifier` places user-controlled transaction description in a clearly delimited block (`"""{description}"""`), never interpolated directly into the instruction segment of the prompt.
- **Couple-level isolation**: `ReportsController` and `BudgetController` (PATCH /income) both apply `[RequireCouple]` filter and derive `coupleId` from JWT claims, not from the request body.
- **MIME validation**: `OcrController.Upload()` must validate `ContentType == "application/pdf"` (or `image/jpeg`/`image/png`) and reject with `400 INVALID_FILE_TYPE` before writing to disk.
- **File size cap**: 20 MB enforced by `ImportJobService` before storage write. ASP.NET Core `MaxRequestBodySize` should also be capped in middleware.

---

## Testing Strategy Overview

- **Unit tests** (in `CoupleSync.UnitTests`):
  - `LocalPdfParserProviderTests` — uses fixture PDF byte arrays (text-based).
  - `BankDetectorTests` — fixture extracted-text strings for each bank; verifies correct parser selected.
  - `InterBankParserTests`, `NubankParserTests`, `ItauParserTests`, `SantanderParserTests`, `BancoBrasilParserTests` — fixture text strings; verify `ParsedTransaction` output shape.
  - `ChatContextServiceTests` — verifies goals section present/absent based on active goal count.
  - `GeminiCategoryClassifierTests` — Gemini HTTP mocked; verifies prompt shape and failure-safe null return.

- **Integration tests** (in `CoupleSync.IntegrationTests`):
  - `OcrProviderRegistrationTests` — verifies correct `IOcrProvider` resolved based on `USE_LOCAL_PDF_PARSER`.
  - `OcrProcessingServiceTests.AutoCategorizes_Transactions` / `ImportSucceeds_WhenGeminiCallFails`.
  - `ReportsControllerTests` — couple-scoped data isolation, 401 without auth, empty-array zero-state.
  - `BudgetController_PatchIncomeTests` — creates plan when absent, updates when present.

- **Static check**: Post-implementation `grep -r "Alert.alert" mobile/app/ mobile/src/ --include='*.tsx' --include='*.ts'` must return zero results in `onError` handlers.
