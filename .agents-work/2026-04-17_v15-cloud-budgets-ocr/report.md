# CoupleSync V1.5 — Final Report

**Session**: `2026-04-17_v15-cloud-budgets-ocr`  
**Status**: ✅ DONE  
**Test suite**: 316 tests (185 unit + 130 integration + 1 E2E), 0 failures  
**TypeScript**: 0 errors  

---

## Summary

V1.5 delivers 5 macro features on top of the V1 foundation (219 tests → 316 tests):

| Macro | Tasks | Status |
|-------|-------|--------|
| MACRO-001 Cloud Migration | T-100 → T-105 | ✅ Complete |
| MACRO-002 Budget Management | T-110 → T-117 | ✅ Complete |
| MACRO-003 OCR Import | T-120 → T-128 | ✅ Complete |
| MACRO-004 UI Polish | T-130 → T-135 | ✅ Complete |
| MACRO-005 AI Chat | T-140 → T-145 | ✅ Complete |

---

## MACRO-001: Cloud Migration
- **Dockerfile** (multi-stage Alpine), **docker-compose.yml**, health endpoint `/health`
- **CI/CD**: `ci.yml` (build+test+tsc+trufflehog+docker), `deploy.yml` (OIDC→GHCR→ACA), `mobile-apk.yml` (EAS→Firebase)
- **DatabaseConnectionResolver**: auto-detects Neon.tech, SslMode=Require
- **Pilot runbook**: `docs/pilot-runbook.md`

## MACRO-002: Budget Management
- **BudgetPlan + BudgetAllocation** entities with couple-level isolation
- **BudgetService**: upsert, allocations (max 20), gap analysis, concurrency handling
- **BudgetController**: POST/GET endpoints, AlertPolicyService BUDGET_EXCEEDED rule
- **Mobile**: Budget setup + overview screen with progress bars
- **Tests**: 22 budget-specific tests

## MACRO-003: OCR Import
- **ImportJob** entity with status machine (Pending→Processing→Ready→Failed→Confirmed)
- **FileTypeDetector**: magic byte validation (JPEG/PNG/PDF)
- **AzureDocumentIntelligenceAdapter**: REST API with stub fallback
- **OcrBackgroundJob**: BackgroundService polling, quota-aware
- **OcrProcessingService**: candidate parsing, SHA-256 dedup
- **OcrController**: upload (10MB), status, results, confirm
- **Mobile**: Camera/document picker upload, exponential backoff polling, review screen
- **Tests**: 40+ OCR-specific tests

## MACRO-004: UI Polish
- **Theme system**: `mobile/src/theme/index.ts` — all tokens (colors, spacing, typography, etc.)
- **Shared components**: LoadingState, EmptyState, ErrorState
- **Animations**: react-native-reanimated AnimatedTabIcon with spring
- **Haptics**: expo-haptics on create/update/confirm actions
- **Tap targets**: All CTAs verified ≥48dp

## MACRO-005: AI Chat
- **IGeminiAdapter + GeminiChatAdapter**: REST call to Google AI Studio, `x-goog-api-key` header, 429/401/5xx handling
- **GeminiOptions**: Model (gemini-2.0-flash), MaxTokens (1024), ApiKey, Enabled flag
- **ChatContextService**: system prompt with budget + spending + professional advice disclaimer
- **ChatRateLimiter**: per-couple 30 req/hour sliding window (singleton)
- **ChatController**: POST /api/v1/ai/chat, feature flag gating (404 when disabled)
- **ChatRequestValidator**: Message ≤2000 chars, History ≤20 items, Role validation
- **Mobile**: Chat screen with message bubbles, ephemeral history (sliced to 20), EXPO_PUBLIC_AI_CHAT_ENABLED tab visibility
- **Tests**: 31 AI chat tests (22 unit + 9 integration)

---

## Known Issues
None.

## Environment Variables (new in V1.5)

| Variable | Required | Default | Purpose |
|----------|----------|---------|---------|
| AI_CHAT_ENABLED | No | false | Enable AI chat endpoint |
| GEMINI_API_KEY | When AI enabled | — | Google AI Studio API key |
| GEMINI_MODEL | No | gemini-2.0-flash | Gemini model ID |
| AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT | No | — | OCR provider endpoint |
| AZURE_DOCUMENT_INTELLIGENCE_KEY | No | — | OCR provider key |
| EXPO_PUBLIC_AI_CHAT_ENABLED | No | — | Show chat tab in mobile |

## Run Instructions

### Backend
```bash
cd backend
dotnet restore
dotnet build
dotnet test
dotnet run --project src/CoupleSync.Api
```

### Mobile
```bash
cd mobile
npm install
npx expo start
```

### Docker
```bash
docker-compose up --build
```
