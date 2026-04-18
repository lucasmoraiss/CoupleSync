# CoupleSync V1 Architecture

## Overview
CoupleSync V1 uses a modular monolith architecture: an Expo React Native Android-first client captures bank transaction notifications on-device through Android `NotificationListenerService`, parses them locally with bank-specific regex patterns, and sends normalized transaction events to a single .NET 8 Web API backed by PostgreSQL. Firebase Cloud Messaging (FCM) remains the outbound notification channel. The design prioritizes delivery speed and trust for pilot scale (up to 10 users / 5 couples), while enforcing strict couple-level isolation and zero storage of raw banking credentials.

## System Layers
- Presentation layer
  - Mobile app (Expo React Native): onboarding, dashboard, goals, projections, alerts UI, and integration permission UX.
  - API controllers: versioned REST endpoints for mobile workflows and transaction event ingestion.
- Application layer
  - Use-case services per module (Auth, Couple, NotificationCapture, Transaction, Goal, CashFlow, Notification).
  - Validation, authorization checks, deduplication, parser orchestration metadata, and alert processing.
- Domain layer
  - Core entities, value objects, and business rules (couple membership, transaction normalization, goal progression, alert thresholds).
- Infrastructure layer
  - EF Core repositories with PostgreSQL.
  - FCM adapter, in-process background workers for alert evaluation/dispatch, sanitization and anti-abuse guards.

## Modules And Responsibilities

### Auth Module
- Register/login/refresh token flows.
- Password hashing and JWT issuance.
- Membership claim embedding in access token (`user_id`, `couple_id`, `roles`).

### Couple Module
- Couple creation and join-code pairing.
- Couple membership validation and lifecycle constraints (max two partners for V1 core coupling model).
- Shared workspace metadata retrieval.

### NotificationCapture Module
- Manage bank-notification integration state (permission enabled, supported bank mappings, last event timestamp).
- Receive parsed transaction events from mobile (`POST /integrations/events`).
- Validate and sanitize every incoming field, enforce authenticated couple scope, deduplicate events, and persist normalized transactions.
- Provide integration status and recovery hints (permission disabled, parser mismatch, upload failure).

### Transaction Module
- Upsert ingested transactions idempotently.
- Deduplication using deterministic event fingerprints and couple/account scope.
- Category assignment (rule-based + user override fields).

### Goal Module
- CRUD and archive for savings goals.
- Progress calculation using current balances and goal contributions.
- Deadline validation and status transitions.

### CashFlow Module
- 30-day and 90-day projection computation.
- Recurrence modeling from historical transactions and configured due dates.
- Confidence and assumptions metadata for non-guaranteed estimates.
- Account balance treated as last known manual/estimated value, not real-time bank balance.

### Notification Module
- Alert policy evaluation (low balance, large transaction, upcoming bill).
- FCM dispatch orchestration and delivery status tracking.
- Device token registration/rotation handling.

## Mobile Notification Listener Design
- Android integration flow
  - User enables Android notification access for CoupleSync from in-app settings.
  - A native Android `NotificationListenerService` bridge captures notifications from supported banking apps (for example: Nubank, Itau, Inter, Bradesco, C6 Bank).
  - Listener filters by package name + notification channel/content guards, then forwards candidate payload to parser.
- Parser pipeline
  - Normalize source text (trim, collapse whitespace, locale-aware decimal normalization).
  - Apply bank-specific regex pattern sets to extract `amount`, `description`, `merchant`, and optional transaction reference.
  - Create `TransactionEvent` with `bank`, `timestamp`, and `rawNotificationText` (sanitized, capped length).
  - If parse confidence is low, mark event as rejected locally and surface in integration diagnostics.
- Pattern maintenance
  - Bank regex patterns versioned in mobile config (`notification-patterns.json`) with fallback generic pattern.
  - Pattern updates shipped via app release for V1.

## Data Flow
- User grants notification listener permission in mobile app.
- Android listener captures supported bank push notification.
- Mobile parser extracts structured transaction fields using bank regex mapping.
- Mobile sends authenticated `TransactionEvent` to backend endpoint `POST /api/v1/integrations/events`.
- Backend NotificationCapture validates schema, sanitizes string content, verifies couple scope from JWT, deduplicates, and persists transaction.
- Transaction domain emits `TransactionsCaptured` internal event.
- Notification module evaluates alert rules and dispatches FCM when conditions match.
- Dashboard and cash flow endpoints read normalized transaction history and last known balances.

## Data Model (High-Level)
- Couple
  - `id`, `join_code`, `created_at`, `status`
- User
  - `id`, `couple_id`, `email`, `name`, `password_hash`, `created_at`, `is_active`
- DeviceToken
  - `id`, `user_id`, `fcm_token`, `platform`, `last_seen_at`
- NotificationCaptureIntegration
  - `id`, `couple_id`, `status`, `permission_granted`, `supported_banks_json`, `last_event_at`, `last_error`
- Account
  - `id`, `couple_id`, `owner_user_id`, `institution_name`, `type`, `currency`, `balance`, `balance_source`, `updated_at`
- TransactionEventIngest
  - `id`, `couple_id`, `user_id`, `bank`, `event_timestamp`, `fingerprint`, `raw_notification_text_redacted`, `ingest_status`, `error`
- Transaction
  - `id`, `couple_id`, `account_id`, `source_event_id`, `amount`, `currency`, `occurred_at`, `description`, `merchant`, `category`, `is_shared`, `metadata_json`
- Goal
  - `id`, `couple_id`, `title`, `target_amount`, `current_amount`, `deadline`, `status`, `archived_at`
- CashFlowSnapshot
  - `id`, `couple_id`, `horizon_days`, `generated_at`, `projection_json`
- NotificationEvent
  - `id`, `couple_id`, `type`, `payload_json`, `scheduled_for`, `sent_at`, `delivery_status`, `dedupe_key`
- JobRun
  - `id`, `couple_id`, `job_type`, `started_at`, `ended_at`, `status`, `metrics_json`, `error`

## Data Isolation Boundary
- Every business table contains `couple_id` except global auth tables that do not hold financial data.
- Every read/write query is filtered by authenticated `couple_id` and verified against route resource ownership.
- Incoming event payloads never provide authoritative `couple_id`; backend derives scope from JWT claims only.
- Cross-couple access attempts return `403` and are logged with audit metadata.

## API Surface (V1)
Base path: `/api/v1`

### Auth Contracts
- `POST /auth/register`
  - Input: `email`, `name`, `password`
  - Output: `user`, `accessToken`, `refreshToken`
- `POST /auth/login`
  - Input: `email`, `password`
  - Output: `accessToken`, `refreshToken`, `user`
- `POST /auth/refresh`
  - Input: `refreshToken`
  - Output: `accessToken`

### Couple Contracts
- `POST /couples`
  - Input: optional `name`
  - Output: `coupleId`, `joinCode`
- `POST /couples/join`
  - Input: `joinCode`
  - Output: `coupleId`, `members`
- `GET /couples/me`
  - Output: couple workspace summary

### NotificationCapture Contracts
- `POST /integrations/events`
  - Input: `amount`, `description`, `merchant`, `bank`, `timestamp`, `rawNotificationText`, optional `currency`, optional `sourceAccountHint`
  - Output: `eventId`, `transactionId`, `ingestStatus`
  - Security: authenticated only; server enforces couple scope from token, validates amount format and timestamp bounds, sanitizes strings, applies dedupe fingerprint.
- `GET /integrations`
  - Output: integration status with permission state, last event timestamp, and last error/recovery hint
- `PATCH /integrations/notification-capture/settings`
  - Input: supported banks/preferences metadata
  - Output: updated settings snapshot

### Transaction Contracts
- `GET /transactions?from=YYYY-MM-DD&to=YYYY-MM-DD&accountId=&category=`
  - Output: paginated transactions
- `PATCH /transactions/{transactionId}/category`
  - Input: `category`
  - Output: updated transaction

### Goal Contracts
- `POST /goals`
- `GET /goals`
- `PATCH /goals/{goalId}`
- `POST /goals/{goalId}/archive`

### CashFlow Contracts
- `GET /cashflow?horizon=30|90`
  - Output: projection timeline and summary totals

### Notification Contracts
- `POST /devices/token`
  - Input: `fcmToken`, `platform`
  - Output: registration status
- `GET /notifications/settings`
- `PATCH /notifications/settings`

### Internal Events And Functions
- Events
  - `NotificationPermissionGranted`, `TransactionEventReceived`, `TransactionEventRejected`, `TransactionsCaptured`, `LargeTransactionDetected`, `LowBalanceDetected`, `BillReminderDue`, `NotificationDispatchRequested`
- Core internal functions
  - `NotificationEventIngestService.Receive(coupleId, userId, payload)`
  - `TransactionService.UpsertFromNotificationEvent(coupleId, eventPayload)`
  - `AlertPolicyService.Evaluate(coupleId, changedTransactions)`
  - `NotificationDispatcher.Send(coupleId, notificationEventId)`

## Background Job Design
- Scheduler model
  - In-process scheduler hosted in backend (`IHostedService`) with cron-like schedules in configuration.
  - No bank polling jobs; ingest is event-driven from mobile uploads.
- Job types
  - `alert-evaluator`: reevaluates thresholds on schedule and after transaction ingestion.
  - `notification-dispatcher`: sends pending events through FCM with retry policy.
- Reliability rules
  - Idempotent ingest via unique event fingerprint per couple.
  - Exponential backoff retries for transient API/FCM failures.
  - Dead-letter status after max retries with user-visible integration status.
  - Distributed locking not required for V1 single backend instance; optimistic row-level concurrency used for overlap safety.

## Deployment Topology (Pilot)
- Mobile
  - Expo React Native app with Android native module bridge for `NotificationListenerService`, distributed via internal APK or Play Internal Testing.
- Backend
  - Single .NET 8 Web API instance on simple PaaS (for example Render or Railway).
- Database
  - Single PostgreSQL instance with automated daily backup and point-in-time restore enabled if available.
- Secrets
  - Injected by platform environment variables: JWT keys, DB connection string, FCM credentials.
- Observability
  - Structured logs + health endpoints (`/health/live`, `/health/ready`) + ingest metrics (accepted/rejected/duplicate events by bank).

## Directory Layout Proposal

### backend/
- `backend/CoupleSync.sln`
- `backend/src/CoupleSync.Api/`
  - `Controllers/`
  - `Contracts/` (request/response DTOs)
  - `Middleware/` (error handling, auth context)
  - `Program.cs`
- `backend/src/CoupleSync.Application/`
  - `Auth/`, `Couple/`, `NotificationCapture/`, `Transaction/`, `Goal/`, `CashFlow/`, `Notification/`
  - `Common/` (abstractions, validators, sanitizers)
- `backend/src/CoupleSync.Domain/`
  - `Entities/`, `ValueObjects/`, `Events/`, `Policies/`
- `backend/src/CoupleSync.Infrastructure/`
  - `Persistence/` (EF Core DbContext, mappings, repositories)
  - `Integrations/Fcm/`
  - `Security/` (input sanitization, anti-abuse policies)
  - `BackgroundJobs/`
- `backend/tests/`

### mobile/
- `mobile/app/` (Expo Router screens)
  - `(auth)/`, `(main)/dashboard`, `(main)/goals`, `(main)/cashflow`, `(main)/settings`
- `mobile/src/modules/`
  - `auth/`, `couple/`, `integrations/`, `transactions/`, `goals/`, `cashflow/`, `notifications/`
- `mobile/src/modules/integrations/notification-capture/`
  - `notificationListenerBridge.ts`, `notificationParser.ts`, `notification-patterns.json`, `eventUploader.ts`
- `mobile/src/services/`
  - `apiClient.ts`, `authStorage.ts`, `pushTokenService.ts`
- `mobile/src/state/`
  - `sessionStore.ts`, `dashboardStore.ts`
- `mobile/src/components/`
- `mobile/src/theme/`
- `mobile/src/utils/`

## Error Handling Strategy
- API error envelope: `code`, `message`, `details`, `traceId`.
- Use domain-specific codes (`COUPLE_SCOPE_VIOLATION`, `EVENT_PAYLOAD_INVALID`, `EVENT_DUPLICATE`, `NOTIFICATION_PERMISSION_MISSING`).
- Client parse/upload failures are mapped to user-safe status and recovery steps.
- Validation errors return `400`; auth errors `401/403`; conflict errors `409`; transient downstream failures `503` with recoverable hint.

## Configuration Strategy
- Environment-based config only, no hardcoded secrets.
- Strongly typed options per concern:
  - `JwtOptions`, `DatabaseOptions`, `FcmOptions`, `NotificationCaptureOptions`, `AlertOptions`.
- Per-environment values:
  - `appsettings.Development.json` for non-sensitive defaults.
  - Secret values injected from environment variables in all environments.
- Mobile parser config:
  - Bank regex patterns versioned in app source for V1; optional remote override disabled by default.

## Security Considerations
- JWT access tokens signed with rotated secret and short TTL; refresh tokens hashed at rest.
- Couple-level authorization enforced in controller policies and repository filters.
- `POST /integrations/events` treats mobile payload as untrusted input: strict schema validation, amount parsing guardrails, timestamp sanity checks, and string sanitization before persistence.
- No raw banking credentials are collected or stored; integration relies only on user-granted Android notification access.
- TLS required in transit; financial payload logging is redacted and raw notification text is truncated/sanitized.

## Testing Strategy Overview
- Unit tests for parser validation rules, transaction dedupe, projection logic, and alert policies.
- Integration tests for API + PostgreSQL + auth/couple scoping checks.
- Contract tests for mobile event payload and backend ingest endpoint.
- Background worker tests for retry behavior and duplicate prevention.
- Manual acceptance walkthrough maps directly to AC-001 through AC-012.

## Minimal Integration Plan
1. Keep Auth and Couple backend contracts unchanged.
2. Implement mobile notification listener bridge + parser + event uploader module.
3. Implement backend NotificationCapture ingestion endpoint with validation, sanitization, and deduplication.
4. Keep Transaction and Dashboard reads over normalized transaction records.
5. Keep Goal and CashFlow modules with 30/90-day computation using last known balances.
6. Keep Notification module with FCM token registration and alert dispatch.
7. Execute pilot deployment and manual acceptance checks for permission flow, event ingest, and alert outcomes.

## Planner Handoff Notes
- The architecture remains modular and can be planned as 6 to 10 macro tasks with 3 to 7 micro tasks each by module.
- Recommended macro grouping for planning: Foundation/Auth/Couple, NotificationCapture ingest flow, Transactions+Dashboard, Goals, CashFlow, Notifications, Observability+Deployment.
