# CoupleSync Backend API

A .NET 8 Web API for CoupleSync, a couple's budgeting and financial planning app. This backend handles authentication, couple management, transaction ingestion from mobile notifications, cash flow projections, goals tracking, and push notifications via Firebase Cloud Messaging.

## Features

- **JWT-based authentication** with refresh token rotation
- **Couple-scoped data isolation** — enforced at middleware and repository levels
- **Transaction ingestion** from Android push notifications with deduplication
- **Automated categorization** of transactions using rule-based patterns
- **Dashboard aggregation** — net worth, shared expenses, per-partner breakdown
- **Savings goals** — CRUD, progress tracking, and archival
- **Cash flow projections** — 30-day and 90-day forecasts with assumptions
- **Alert policies** — low balance, large transaction, upcoming bills
- **Firebase Cloud Messaging (FCM)** integration for push notifications
- **PostgreSQL persistence** with Entity Framework Core

## Requirements

- **.NET 8 SDK** (download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download))
- **PostgreSQL 13+** (local or managed service)
- **Firebase Project** (for FCM credentials)
- Environment variables for secrets (see Configuration section)

## Quick Start

### 1. Clone the Repository

```bash
git clone https://github.com/lucasmoraiss/CoupleSync.git
cd CoupleSync/backend
```

### 2. Install Dependencies

```bash
dotnet restore
```

### 3. Configure Environment

Create a `.env.local` file or set environment variables. See the **Environment Variables** section below.

**Minimal example:**
```bash
export DATABASE_URL="Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=postgres"
export JWT__SECRET="a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6"
export FIREBASE_PROJECT_ID="couplesync-pilot"
export FIREBASE_CREDENTIAL_JSON="{...service account json...}"
```

### 4. Initialize Database

```bash
# Apply Entity Framework migrations
dotnet ef database update --startup-project src/CoupleSync.Api

# This creates all tables and seeds category rules for transaction auto-categorization
```

### 5. Run the API

```bash
# Development mode
dotnet run --project src/CoupleSync.Api

# Production mode
dotnet publish -c Release -o ./publish
dotnet ./publish/CoupleSync.Api.dll
```

The API will start on `https://localhost:5001` (or `http://localhost:5000` for HTTP).

### 6. Test the API

```bash
# Register a new user
curl -X POST https://localhost:5001/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email":"pilot@couplesync.local",
    "name":"Pilot User",
    "password":"TestPass123!"
  }'

# Login
curl -X POST https://localhost:5001/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{
    "email":"pilot@couplesync.local",
    "password":"TestPass123!"
  }'
```

## Environment Variables

| Variable | Required | Example | Notes |
|----------|----------|---------|-------|
| `DATABASE_URL` | Yes | `Host=localhost;Port=5432;Database=couplesync;Username=postgres;Password=postgres` | PostgreSQL connection string. Used in Program.cs to configure EF Core. |
| `JWT__SECRET` | Yes | `a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6` | Min 32 characters. Used to sign JWT tokens. Generate with: `openssl rand -hex 16` |
| `JWT__ISSUER` | No | `CoupleSync` | Defaults to value in appsettings.json. Must match appsettings.json. |
| `JWT__AUDIENCE` | No | `CoupleSync.Mobile` | Defaults to value in appsettings.json. Must match appsettings.json. |
| `JWT__ACCESSTOKENTTLMINUTES` | No | `15` | Defaults to 15. Access token lifetime in minutes. |
| `JWT__REFRESHTOKENTTLDAYS` | No | `7` | Defaults to 7. Refresh token lifetime in days. |
| `FIREBASE_PROJECT_ID` | Yes | `couplesync-pilot` | Your Firebase project ID. Required for FCM. |
| `FIREBASE_CREDENTIAL_JSON` | Yes | `{"type":"service_account",...}` | Firebase Admin SDK service account JSON (base64-encoded or raw JSON). Required for FCM dispatch. DO NOT commit. |

## Project Structure

```
backend/
├── src/
│   └── CoupleSync.Api/                   # Web API layer
│       ├── Controllers/                  # REST endpoints
│       ├── Contracts/                    # DTOs and request/response models
│       ├── Filters/                      # Custom action filters
│       ├── Health/                       # Health check endpoints
│       ├── Middleware/                   # Authorization, couple-scope enforcement
│       ├── Program.cs                    # DI container setup and middleware config
│       ├── appsettings.json              # Config defaults (DO NOT add secrets)
│       └── appsettings.Development.json
│   ├── CoupleSync.Application/           # Use-case / service layer
│       ├── Auth/                         # Auth service and token handlers
│       ├── Couple/                       # Couple creation and membership
│       ├── NotificationCapture/          # Transaction event ingestion
│       ├── Transaction/                  # Transaction queries and updates
│       ├── Goal/                         # Goal CRUD and progress
│       ├── CashFlow/                     # Projection service
│       ├── Dashboard/                    # Aggregation queries
│       └── Notification/                 # Alert policies and FCM coordination
│   ├── CoupleSync.Domain/                # Domain entities and business rules
│       ├── Entities/                     # Couple, User, Transaction, Goal, etc.
│       ├── Interfaces/                   # Repository and service interfaces
│       ├── Policies/                     # Business rule implementations
│       └── ValueObjects/                 # Immutable value types
│   └── CoupleSync.Infrastructure/        # Data access and external integrations
│       ├── Persistence/                  # EF Core DbContext and migrations
│       ├── Migrations/                   # Database schema migrations
│       ├── Security/                     # Password hashing and JWT utilities
│       ├── Integrations/                 # Firebase FCM adapter
│       └── BackgroundJobs/               # Notification dispatcher worker
├── tests/
│   ├── CoupleSync.UnitTests/             # Domain and service layer unit tests
│   ├── CoupleSync.IntegrationTests/      # API and persistence integration tests
│   └── CoupleSync.E2ETests/              # End-to-end pilot flow tests
└── CoupleSync.sln                        # Visual Studio solution file
```

## Running Tests

```bash
# Run all tests
dotnet test

# Run tests for a specific category
dotnet test --filter Auth
dotnet test --filter Couple
dotnet test --filter Transaction
dotnet test --filter Dashboard
dotnet test --filter Goal
dotnet test --filter CashFlow
dotnet test --filter Notification

# Run with code coverage
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
```

## Build and Deploy

### Development Build

```bash
dotnet build -c Debug
```

### Release Build

```bash
dotnet publish -c Release -o ./publish
```

**Output:** The `publish/` directory contains all binaries and config files needed to run the API.

### Deploy to Cloud

See [docs/deployment/pilot-runbook.md](../docs/deployment/pilot-runbook.md) for complete step-by-step deployment instructions including:
- Firebase project setup and service account generation
- PostgreSQL database initialization
- Backend API deployment to Azure App Service, AWS EC2, or self-hosted
- Environment variable configuration
- Android APK distribution via Google Play Internal Testing
- Validation checklist

## API Endpoints

### Auth
- `POST /api/v1/auth/register` — Create a new user account
- `POST /api/v1/auth/login` — Authenticate and receive JWT tokens
- `POST /api/v1/auth/refresh` — Refresh an expired access token

### Couple
- `POST /api/v1/couples` — Create a new couple workspace
- `POST /api/v1/couples/join` — Join a couple with a code
- `GET /api/v1/couples/me` — Get current user's couple metadata

### Transactions
- `GET /api/v1/transactions` — List transactions with filtering
- `PATCH /api/v1/transactions/{id}/category` — Update transaction category

### Dashboard
- `GET /api/v1/dashboard` — Get aggregate net worth, expenses, and balances

### Notifications
- `POST /api/v1/integrations/events` — Ingest transaction event from mobile
- `GET /api/v1/integrations` — Get notification capture integration status
- `POST /api/v1/notifications/device-tokens` — Register FCM device token
- `GET /api/v1/notifications/settings` — Get alert policy settings
- `PATCH /api/v1/notifications/settings` — Update alert policy settings

### Goals
- `POST /api/v1/goals` — Create a savings goal
- `GET /api/v1/goals` — List goals for the couple
- `GET /api/v1/goals/{id}` — Get a specific goal
- `PATCH /api/v1/goals/{id}` — Update a goal
- `DELETE /api/v1/goals/{id}` — Archive a goal

### CashFlow
- `GET /api/v1/cashflow` — Get 30 and 90-day projections with assumptions

## Security Considerations

- **Couple-Scoped Isolation:** Every endpoint enforces couple-level data isolation via JWT `couple_id` claim. See [Middleware/CoupleAuthorizationMiddleware.cs](#) for implementation.
- **No Banking Credentials Stored:** The app uses Android NotificationListenerService to read bank push notifications. Raw banking credentials (passwords, tokens) are never collected or stored.
- **Secrets Management:** All sensitive values (JWT secret, database password, Firebase credentials) come from environment variables — never hardcoded in source.
- **Password Hashing:** User passwords are hashed using bcrypt with a cost factor of 11 (configurable in Security/PasswordHasher.cs).

## Troubleshooting

### API Won't Start — Database Connection Error

```
InvalidOperationException: Unable to connect to database
```

**Solution:**
1. Verify PostgreSQL is running: `psql -h localhost -U postgres -c "SELECT 1"`
2. Verify DATABASE_URL is set: `echo $DATABASE_URL`
3. Verify connection string syntax: `Host=<host>;Port=5432;Database=<db>;Username=<user>;Password=<pass>`

### Migration Fails — Cannot Apply Migration

```
Npgsql.PostgresException: relation "Users" already exists
```

**Solution:**
1. If you're running migrations on an already-initialized database, you may need to drop and recreate:
   ```sql
   DROP DATABASE IF EXISTS couplesync;
   CREATE DATABASE couplesync;
   ```
2. Then re-run: `dotnet ef database update`

### Firebase Credentials Invalid

```
FirebaseException: Credential does not contain the required fields
```

**Solution:**
1. Verify FIREBASE_CREDENTIAL_JSON is set correctly (not truncated or malformed)
2. If base64-encoded, decode to verify JSON is valid: `echo $FIREBASE_CREDENTIAL_JSON | base64 -d | jq .`
3. Ensure the service account is from the correct Firebase project
4. Check Firebase Console Project Settings that the account has "Editor" role

## Contributing

1. Create a feature branch: `git checkout -b feature/your-feature`
2. Write tests for new code (unit and integration)
3. Ensure all tests pass: `dotnet test`
4. Commit with a clear message: `git commit -am "feat: add your feature"`
5. Push to your fork and create a Pull Request

## License

CoupleSync is proprietary software. See LICENSE file for details.

## Support

For issues, questions, or deployment help, see the full [Pilot Deployment Runbook](../docs/deployment/pilot-runbook.md).
