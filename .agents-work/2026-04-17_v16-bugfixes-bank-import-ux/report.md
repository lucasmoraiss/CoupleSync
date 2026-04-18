# CoupleSync V1.6 — Final Report

**Session**: `2026-04-17_v16-bugfixes-bank-import-ux`  
**Status**: DONE  
**Test suite**: 391 tests (254 unit + 136 integration + 1 E2E), 0 failures  
**TypeScript**: 0 errors  

---

## Summary

V1.6 fixes critical bugs from V1.5 testing, replaces the broken Azure receipt OCR with a local bank statement PDF parser, overhauls error UX, and adds high-value features.

---

## MACRO-P0: Critical Bugfixes (T-600)

| Fix | Root Cause | Solution |
|-----|-----------|----------|
| OCR upload fails | Manual `Content-Type: multipart/form-data` header in apiClient.ts strips boundary | Removed header — React Native XHR auto-sets boundary |
| Docker file write fails | `/app/uploads` owned by root, appuser can't write | `RUN mkdir -p /app/uploads && chown appuser:appgroup /app/uploads` |
| Azure DI not configured | Missing env vars in docker-compose | Added `AZURE_DOCUMENT_INTELLIGENCE_*`, `USE_LOCAL_PDF_PARSER`, `Storage__BasePath`, volume mount |

## MACRO-P1: UX Improvements (T-610–T-612)

- **ToastProvider + Toast component**: Custom themed toast system with 4 variants (success/error/warning/info), auto-dismiss 4s, tap-to-dismiss, proper accessibility (`assertive` for errors)
- **Alert.alert() eliminated**: All mutation `onError` handlers across budget, OCR, goals, transactions now use styled toasts
- **Budget ErrorState**: Budget query failures render `<ErrorState>` with retry instead of spinner
- **403 COUPLE_REQUIRED**: Global interceptor shows warning toast + redirects to couple setup screen
- **useMemo optimization**: Toast hook memoized to prevent unnecessary re-renders

## MACRO-P2: Bank Statement Parser (T-620–T-626)

- **PdfPig** (Apache 2.0): Pure .NET PDF text extraction, no external dependencies
- **7 bank parsers**: Nubank, Inter, Banco do Brasil, Itaú, Santander, Caixa, Mercantil
- **BankDetector**: Strategy pattern — `IBankStatementParser.CanParse()` chain auto-detects bank format
- **BrazilianCurrencyParser**: Shared helper for R$ amount parsing (thousand dots, decimal commas)
- **LocalPdfParserProvider**: Full `IOcrProvider` implementation replacing Azure Document Intelligence
- **Feature flag**: `USE_LOCAL_PDF_PARSER=true` (default) — Azure adapter preserved for rollback
- **Security**: Path traversal guard in `DownloadAsync`, no raw text logging (NFR-004)
- **OcrCandidate.TransactionType**: Credit/Debit classification flows through the full pipeline

## MACRO-P3: Feature Enhancements (T-640–T-646)

### Quick Income (T-640 + T-641)
- `PATCH /api/v1/budget/income` — auto-creates BudgetPlan for current month if none exists
- Mobile QuickIncomeModal on Dashboard — one-tap income update
- Concurrent-create protection (DbUpdateException/23505 catch)

### AI Auto-Categorization (T-642 + T-643)
- `ICategoryClassifier` interface (Application layer) + `GeminiCategoryClassifier`
- Structured delimited prompt (triple-double-quote) — injection-safe per ADR-004
- Couple-specific categories from budget allocations (via `SuggestCategoryAsync`)
- 3-second per-call timeout + fail-fast after 2 consecutive errors
- `NullCategoryClassifier` fallback when AI_CHAT_ENABLED=false
- Wired into OcrProcessingService (background job stage) — categories visible on review screen

### Goals in AI Chat (T-644)
- ChatContextService now injects goal name, target, progress, deadline into system prompt
- Goal titles sanitized (strip newlines, trim 100 chars) — anti-injection

### Visual Reports (T-645 + T-646)
- `GET /api/v1/reports/spending-by-category` — spending by category with deterministic colors
- `GET /api/v1/reports/monthly-trends` — income vs expense by month
- Mobile Reports screen with PieChart (donut) + BarChart (horizontal scroll)
- Period selector (3/6/12 months)
- react-native-gifted-charts + react-native-svg

## MACRO-P4: Documentation (T-660)

- **docs/guia-de-uso.md**: Comprehensive app usage guide in Portuguese
- Covers all 10 features, daily workflow, competitor comparison, FAQs
- Competitor analysis: Mobills, Organizze, YNAB, Splitwise, Honeydue

---

## New Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| USE_LOCAL_PDF_PARSER | true | Use PdfPig instead of Azure Document Intelligence |
| Storage__BasePath | ./uploads | File storage directory |

---

## Security Improvements

1. Path traversal guard in `LocalFileStorageAdapter.DownloadAsync` (canonicalization)
2. `FileNotFoundException` no longer leaks internal server paths
3. Prompt injection defense: triple-double-quote delimiters + response validation
4. Goal title sanitization in AI system prompt
5. Raw bank statement text never logged (NFR-004)

---

## Run Instructions

### Backend
```bash
cd backend && dotnet restore && dotnet build && dotnet test
dotnet run --project src/CoupleSync.Api
```

### Mobile
```bash
cd mobile && npm install && npx expo start
```

### Docker
```bash
docker-compose up --build
```
