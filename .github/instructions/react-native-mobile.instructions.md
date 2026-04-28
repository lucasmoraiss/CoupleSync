---
description: "Use when writing or modifying React Native / Expo mobile code — screens, components, modules, stores, services, or navigation in mobile/."
applyTo: ["mobile/app/**", "mobile/src/**"]
---
# Mobile Conventions (CoupleSync — React Native / Expo)

## Routing
- Expo Router file-based routing in `mobile/app/`
- Tab layout: `app/(tabs)/` — main navigation screens
- Auth flow: `app/(auth)/` — login, register, onboarding

## Feature Modules
- `src/modules/chat/` — couple chat feature
- `src/modules/integrations/` — bank notification integration
- `src/modules/ocr/` — receipt scanning
- `src/modules/transactions/` — manual transaction entry

## State & Data
- Zustand stores in `src/state/` (`dashboardStore`, `sessionStore`)
- API client: `src/services/apiClient.ts` — Axios-based, JWT auto-refresh
- Push tokens: `src/services/pushTokenService.ts`

## Components
- Shared components in `src/components/` (EmptyState, ErrorBoundary, LoadingState, Toast)
- Theme tokens in `src/theme/` — use theme values, not hardcoded colors/sizes
- Android-first: test on Android emulator, Samsung ergonomics

## Patterns
- TypeScript strict mode — no `any` unless interfacing with untyped native modules
- API types in `src/types/api.ts` — keep in sync with backend DTOs
- Error boundaries wrap feature screens
- Financial values: use neutral language, never judgmental tone
