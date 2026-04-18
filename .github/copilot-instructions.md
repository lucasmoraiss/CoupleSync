# CoupleSync Project Instructions for AI Agents

## Product Context
CoupleSync is an Android-first budgeting app for couples. Initial pilot scope supports up to 10 users (5 couples). Prioritize usability, trust, and speed of iteration over horizontal scalability.

## Preferred Stack
- Mobile: React Native with Expo
- Backend: .NET 8 Web API
- Database: PostgreSQL
- Bank integration: Open Finance provider API
- Notifications: Firebase Cloud Messaging

## Delivery Principles
- Follow spec-driven execution from .agents-work session artifacts.
- Keep changes small and mergeable.
- Preserve clear traceability from code and docs to acceptance criteria IDs.
- Default to secure-by-design implementation patterns.

## Security and Privacy Rules
- Never store raw banking credentials.
- Couple-level data isolation is mandatory for every endpoint and query.
- Secrets must come from environment configuration, never hardcoded.
- Validate all external provider payloads before persistence.

## UX Rules
- Android-first interactions and ergonomics.
- Core actions should be reachable in minimal taps.
- Financial language should be clear, neutral, and non-judgmental.
- Surface integration failures with explicit status and recovery guidance.

## Artifact Discipline
- Keep .agents-work/<session>/spec.md, acceptance.json, tasks.yaml, status.json consistent.
- Update status.json last_update whenever a state change is made.
- Keep tasks.yaml as the single source of truth for task status.

## Scope Guardrails
- V1 excludes iOS, microservices decomposition, and advanced AI-driven categorization.
- Do not expand scope without explicit user decision and artifact updates.
