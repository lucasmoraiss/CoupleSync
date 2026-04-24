# Critical Bugfixes: CI, OCR Errors, Gradient, Production Discrepancies

## Problem Statement

Five urgent defects are blocking the CoupleSync development workflow and degrading the production user experience:

1. **CI pipeline is red** — `CreateCoupleCommandHandler` and `JoinCoupleCommandHandler` now require `IJwtTokenService` in their constructors, but the unit tests still instantiate handlers without it, causing compilation failures in `dotnet test`.
2. **OCR errors are swallowed** — `ocr-upload.tsx` uses bare `catch {}` blocks so password-protected or invalid PDF responses (HTTP 422) show only a generic Portuguese message instead of the server-supplied error description.
3. **Manual transaction entry is hard to find** — The "+" header icon in `transactions/index.tsx` is small and not obviously discoverable; users may not realise manual entry is available.
4. **App crashes at startup in dev when gifted-charts loads** — `react-native-gifted-charts` depends on `expo-linear-gradient` which is absent from `package.json`.
5. **Production build regressions** — Reports tab crashes (same missing dependency as #4) and icons are broken in EAS builds, likely because `@expo/vector-icons` is not being bundled correctly.

---

## Goals

- Restore green CI by fixing unit-test constructor calls.
- Surface descriptive backend error messages from OCR 422 responses on the mobile UI.
- Ensure manual transaction creation is discoverable within 2 taps.
- Add `expo-linear-gradient` to `package.json` so gifted-charts initialises cleanly.
- Eliminate the Reports tab crash and icon breakage in EAS production builds.

---

## Non-Goals

- New features of any kind.
- Architecture changes (no service decomposition, no new projects).
- Design system overhaul (only minimal UX tweak to button visibility if needed).
- iOS support.
- Advanced AI categorisation.

---

## User Stories

| ID | Story |
|----|-------|
| US-01 | As a developer, I want `dotnet test` to pass on CI so that pull-request gates are enforced. |
| US-02 | As a user, when I upload a password-protected PDF, I want to see a clear error message explaining why processing failed. |
| US-03 | As a user, I want to clearly see a way to add a transaction manually without relying solely on OCR. |
| US-04 | As a developer, I want the dev app to start without a gradient-related crash so I can iterate quickly. |
| US-05 | As a user, I want the Reports screen to display charts without crashing on a production build. |
| US-06 | As a user, I want all icons to render correctly in the production APK. |
| US-07 | As a QA engineer, I want acceptance commands to run green in CI so regressions are caught automatically. |

---

## Functional Requirements

### FR-01 — CI Fix (BUG 1)
- `CreateCoupleCommandHandler` tests must pass `StubJwtTokenService` (already at `tests/CoupleSync.UnitTests/Support/StubJwtTokenService.cs`) as the `IJwtTokenService` constructor argument.
- `JoinCoupleCommandHandler` tests must do the same.
- No production code changes required; only test instantiation is broken.

### FR-02 — OCR Error Surfacing (BUG 2)
- `ocr-upload.tsx` `catch` blocks must inspect the thrown error for `error.response?.data?.message`.
- If a server message exists, display it verbatim in the error state.
- If no server message (network error, timeout), retain the current generic fallback string.
- Must handle both `catch` locations (lines 88 and 114).

### FR-03 — Manual Transaction Discoverability (BUG 3)
- The "+" button in `transactions/index.tsx` header must be clearly visible (sufficient size, label, or contrasting icon).
- Alternatively, add a floating action button (FAB) if the header icon is deemed insufficient.
- `transactions/new.tsx` route must remain unchanged.

### FR-04 — Add expo-linear-gradient (BUG 4)
- Add `expo-linear-gradient` to `dependencies` in `mobile/package.json` at a version compatible with the current Expo SDK.
- Run `npx expo install expo-linear-gradient` semantics — do not pin to an incompatible version.

### FR-05 — Production Build Fix (BUG 5)
- Confirm that `expo-linear-gradient` fix (FR-04) resolves the Reports tab crash.
- Verify `@expo/vector-icons` is listed as a direct dependency (or peer-resolved) in `package.json`; add or update if missing.
- No EAS build config changes unless the dependency alone is insufficient.

---

## Non-Functional Requirements

| Category | Requirement |
|----------|-------------|
| Performance | Fixing dependencies must not increase cold-start time by more than 500 ms. |
| Security | No credentials or secrets introduced. OCR error messages forwarded to UI must not expose internal stack traces. |
| Usability | Error messages in the UI must be in Portuguese and must not exceed 2 lines of text. |
| Reliability | `dotnet test` must exit 0. App must launch without uncaught exceptions in Expo Go. |
| Traceability | Each changed file traces to at least one AC in `acceptance.json`. |

---

## Edge Cases

| ID | Edge Case |
|----|-----------|
| EC-01 | OCR endpoint returns a 422 with `message` absent from body — fallback generic message must display. |
| EC-02 | OCR endpoint returns a 500 or network timeout — generic fallback must display (not a crash). |
| EC-03 | `StubJwtTokenService` returns an empty/null token — handler tests that depend on the token value must still assert their primary behaviour (not JWT content). |
| EC-04 | `expo-linear-gradient` install introduces a peer-dependency conflict with the current Expo SDK — version must be resolved to a compatible range. |
| EC-05 | Reports screen is opened when the couple has no transactions — charts render with empty state, no crash. |
| EC-06 | Production build on EAS free tier doesn't bundle `@expo/vector-icons` because it's a transitive dep only — explicit listing in `package.json` is required. |
| EC-07 | Manual transaction button is tapped in landscape orientation — screen layout must not overflow. |
| EC-08 | `JoinCoupleCommandHandler` tests that assert token content (e.g., claims) — `StubJwtTokenService` must return a well-formed token stub so assertions don't false-positive. |
| EC-09 | CI runner has cached a previous test build without the new `StubJwtTokenService` injection — `--no-restore` flag in acceptance command ensures the correct artefact is tested. |

---

## Assumptions

1. `StubJwtTokenService` already implements `IJwtTokenService` and returns a fixed/stub token suitable for unit tests.
2. The backend `GlobalExceptionMiddleware` already serialises `OcrException` as `{ code, message, traceId }` with HTTP 422 — no backend change is needed for FR-02.
3. The Expo SDK version in use is compatible with `expo-linear-gradient` `^12.x` or whichever range `npx expo install` resolves.
4. `@expo/vector-icons` is already used in source but may only be a transitive dependency; adding it explicitly is the fix.
5. The manual transaction route `transactions/new.tsx` is functional — only visibility/discoverability is the problem.
6. All changes land on the `main` branch; no feature branch workflow.
7. No migrations or database schema changes are required by any of these fixes.

---

## Definition of Done

- [ ] `dotnet build --no-restore` exits 0 with zero errors.
- [ ] `dotnet test --no-build` exits 0 — all unit tests pass, including the previously failing `CreateCoupleCommandHandlerTests` and `JoinCoupleCommandHandlerTests`.
- [ ] OCR upload of a password-protected PDF shows a descriptive Portuguese error message sourced from the backend response.
- [ ] Manual transaction "+ button" is visible and navigates to `transactions/new.tsx`.
- [ ] `expo-linear-gradient` is present in `mobile/package.json` dependencies.
- [ ] Dev app starts without a gradient-related runtime exception.
- [ ] Reports tab in a production (EAS) build does not crash.
- [ ] Icons render correctly in a production (EAS) build.
- [ ] All acceptance criteria in `acceptance.json` are marked verified.

---

## Acceptance Criteria

Mapped to `acceptance.json` in this session folder.

| AC ID | Description | Verification |
|-------|-------------|--------------|
| AC-001 | `dotnet build` exits 0 | `cmd: cd backend && dotnet build --no-restore` |
| AC-002 | All unit tests pass | `cmd: cd backend && dotnet test --no-build` |
| AC-003 | OCR error message from backend is displayed on mobile | `manual: upload password-protected PDF → UI shows server error text` |
| AC-004 | Manual transaction button visible and functional | `manual: open Transactions screen → "+" leads to new transaction form` |
| AC-005 | App starts without gradient exception in dev | `manual: launch in Expo Go → no red crash screen` |
| AC-006 | Reports tab does not crash in production build | `manual: EAS build → Reports tab loads charts` |
| AC-007 | Icons render in production build | `manual: EAS build → all icons visible across screens` |
| AC-008 | expo-linear-gradient in package.json | `cmd: grep "expo-linear-gradient" mobile/package.json` |
