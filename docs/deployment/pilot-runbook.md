# CoupleSync Pilot Deployment Runbook

**Last Updated:** April 16, 2026

This runbook provides step-by-step instructions for deploying CoupleSync to a pilot environment. It covers Firebase project setup, backend deployment, database initialization, and Android APK distribution via Google Play Internal Testing.

## Target Audience

- Backend engineers deploying the .NET API and PostgreSQL database
- Mobile engineers building and distributing the Android APK
- DevOps/Platform engineers managing deployment infrastructure

## Scope

- **Includes:** Firebase project creation, backend (.NET 8), PostgreSQL database (Neon.tech), Azure Container Apps deployment, Android build and distribution via Firebase App Distribution, environment secrets management, GitHub Actions CI/CD
- **Excludes:** iOS deployment, multi-region failover, Kubernetes orchestration

## Prerequisites

- **Hosted Environment:** Azure Container Apps (Consumption Plan) with Neon.tech PostgreSQL database
- **Local Development Machine:** 
  - .NET 8 SDK
  - PostgreSQL client tools
  - Node.js 18+ / npm
  - Expo CLI (`npm install -g expo-cli`)
  - Android SDK (for local builds) or EAS account (for cloud builds)
  - git
- **Google/Firebase:** Google account with ability to create Firebase projects
- **Play Store:** Developer account with ability to manage internal testing tracks

---

## 1. Firebase Project Setup

### 1.1 Create Firebase Project

1. Go to [Firebase Console](https://console.firebase.google.com/)
2. Click **+ Add project**
3. Enter project name (e.g., `couplesync-pilot`)
4. Accept terms and click **Continue**
5. Disable Google Analytics (not needed for V1 pilot) and click **Create project**
6. Wait for project provisioning (~1 minute)

**Validation:** You should see the Firebase project dashboard with "Getting started" cards.

### 1.2 Register Android App with Firebase

1. In Firebase Console, click the **Android icon** (or **+ Add app** → Android)
2. Enter package name: `com.couplesync.app`
3. Enter app nickname: `CoupleSync Pilot` (optional)
4. Click **Register app**
5. On the next screen, click **Download google-services.json**
6. Save the file locally
7. Follow the on-screen prompt: *Do not commit this file to version control* (we handle this separately in .gitignore)

**Security Note:** `google-services.json` contains API keys and must NOT be committed to git. See section "Secret Files That Must Be Gitignored" below.

### 1.3 Place google-services.json in Mobile Project

1. In your local clone of the CoupleSync repo, navigate to `mobile/`
2. Place the downloaded `google-services.json` file in `mobile/google-services.json` (root of mobile folder)
3. Verify: `ls -la mobile/google-services.json` should show the file exists
4. Do NOT commit this file (already in .gitignore)

**Validation:** When you run `npx expo prebuild --platform android`, Expo's build process will include this file in the Android project automatically.

### 1.4 Generate Firebase Admin SDK Service Account JSON

Used by the backend to send FCM push notifications.

1. In Firebase Console, go to **Project Settings** (gear icon, top-left)
2. Click the **Service Accounts** tab
3. Choose **Node.js** as the language (for reference; we're using .NET)
4. Click **Generate New Private Key**
5. A JSON file downloads to your machine (e.g., `couplesync-pilot-xxxxx.json`)
6. **Save this file securely** — it will be used only as a secret environment variable (never committed)

**Security Note:** This file contains your Firebase credentials. Treat it like a database password.

### 1.5 Note Your Firebase Project ID

1. Still in **Project Settings**, copy your **Project ID** (e.g., `couplesync-pilot`)
2. You'll need this for the `FIREBASE_PROJECT_ID` environment variable

**Validation:** Your project ID should be visible in Project Settings and also in the Firebase Console URL (`https://console.firebase.google.com/project/<PROJECT_ID>/...`)

---

## 2. Backend Deployment

### 2.1 Prerequisites

- PostgreSQL server deployed (locally, cloud, or managed service)
- Database connection string (format: `Host=<hostname>;Port=5432;Database=couplesync;Username=<user>;Password=<password>`)
- .NET 8 SDK installed

### 2.2 Prepare Environment Variables

Create a `.env` file or configure your hosting platform's environment variables with the following:

| Variable | Source | Example Value | Notes |
|----------|--------|----------------|-------|
| `JWT__SECRET` | Generated (32+ random chars) | `a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6` | Use `openssl rand -hex 16` to generate |
| `JWT__ISSUER` | Fixed | `CoupleSync` | Must match jwt config in appsettings.json |
| `JWT__AUDIENCE` | Fixed | `CoupleSync.Mobile` | Must match jwt config in appsettings.json |
| `JWT__ACCESSTOKENTTLMINUTES` | Fixed | `15` | Access token lifetime |
| `JWT__REFRESHTOKENTTLDAYS` | Fixed | `7` | Refresh token lifetime |
| `DATABASE_URL` | PostgreSQL connection | `Host=db.example.com;Port=5432;Database=couplesync;Username=postgres;Password=xyz123` | Must reference your database server |
| `FIREBASE_PROJECT_ID` | From Firebase Console | `couplesync-pilot` | Your Firebase project ID |
| `FIREBASE_CREDENTIAL_JSON` | From service account JSON | (base64-encoded JSON or raw JSON content) | See 2.3 below |

### 2.3 Encode Firebase Service Account JSON for Environment Variable

The backend expects `FIREBASE_CREDENTIAL_JSON` as a base64-encoded string or as raw JSON content (depending on deployment method).

**Option A: Base64 Encoding (Recommended for secrets management)**
```bash
# Linux/macOS
cat path/to/couplesync-pilot-xxxxx.json | base64

# PowerShell (Windows)
[Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes((Get-Content "path\to\couplesync-pilot-xxxxx.json" -Raw)))
```

Copy the output and set as `FIREBASE_CREDENTIAL_JSON` environment variable.

**Option B: Raw JSON Content**
If your deployment platform supports multi-line environment variables, copy the entire contents of the service account JSON file and set directly.

**Security Caveat:** Do NOT paste the service account JSON into code, config files, or logs. Use environment variables only.

### 2.4 Build and Publish Backend

```bash
# Navigate to backend directory
cd backend

# Restore NuGet packages
dotnet restore

# Build in Release mode
dotnet build -c Release

# (Optional) Run tests to validate
dotnet test --no-build

# Publish for deployment
dotnet publish -c Release -o ./publish
```

**Validation:** The `publish/` directory should contain:
- `CoupleSync.Api.dll`
- `appsettings.json`, `appsettings.Production.json`
- All supporting assemblies

### 2.5 Initialize or Migrate Database

Before starting the API, ensure the database is initialized.

```bash
# From the backend directory, apply Entity Framework migrations
dotnet ef database update --startup-project src/CoupleSync.Api

# Alternatively, if using a CI/CD trigger, you can set this as a release task
```

**What This Does:**
- Creates tables for Users, Couples, Accounts, Transactions, Goals, Notifications, etc.
- Creates indices for performance queries (per T-011)
- Seeds category rules (CategoryRulesSeeder) for transaction auto-categorization

**Validation:** Connect to your PostgreSQL database and verify tables exist:
```sql
SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' LIMIT 10;
```

You should see tables like `"AspNetUsers"`, `"Couples"`, `"Transactions"`, `"Goals"`, etc.

### 2.6 Deploy Published Artifacts

Deployment method depends on your hosting platform:

**Azure App Service Example:**
```bash
# Package the published directory
cd publish
zip -r ../couplesync.zip .
cd ..

# Upload via Azure CLI or App Service Deploy task
az webapp deployment source config-zip --resource-group <rg> --name <app-name> --src couplesync.zip

# Set environment variables in App Service Configuration
az webapp config appsettings set \
  --resource-group <rg> \
  --name <app-name> \
  --settings JWT__SECRET="<generated-secret>" \
              DATABASE_URL="<connection-string>" \
              FIREBASE_PROJECT_ID="<project-id>" \
              FIREBASE_CREDENTIAL_JSON="<base64-encoded-json-or-raw-content>"
```

**AWS EC2 / Self-Hosted Example:**
```bash
# Copy published folder to server
scp -r publish/ ubuntu@<server>:/home/ubuntu/couplesync/

# On server, start the API
# (Assumes .NET 8 runtime is installed)
cd /home/ubuntu/couplesync/publish
export JWT__SECRET="<generated-secret>"
export DATABASE_URL="<connection-string>"
export FIREBASE_PROJECT_ID="<project-id>"
export FIREBASE_CREDENTIAL_JSON="<base64-or-raw>"
./CoupleSync.Api
```

**Validation:** The API should start and log `Application started. Press Ctrl+C to shut down.` or similar. Test connectivity:
```bash
curl -X POST http://<api-host>/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","password":"password"}'
```

You should receive a 400 (invalid credentials) or 200 (if user exists) — not a 500 or connection error.

---

## 3. PostgreSQL Database Setup

### 3.1 Provision PostgreSQL Server

Choose one:

- **Managed Service (Recommended for Pilot):** AWS RDS, Azure Database for PostgreSQL, or Google Cloud SQL
  - Follow the provider's documentation to create a PostgreSQL 13+ instance
  - Note the connection string: `Host=<endpoint>;Port=5432;Database=couplesync;Username=<master-user>;Password=<password>`

- **Self-Hosted:** Install PostgreSQL locally or on a VM
  - Create a database: `createdb couplesync`
  - Create a user: `createuser couplesync_user -P` (prompts for password)
  - Grant permissions: `psql -c "GRANT ALL PRIVILEGES ON DATABASE couplesync TO couplesync_user;"`

### 3.2 Verify Connectivity

```bash
# From your local machine or deployment server
psql -h <database-host> -U <username> -d couplesync -c "SELECT version();"
```

**Validation:** Command should return the PostgreSQL version string without timeout or authentication errors.

### 3.3 Automated Migration via Backend

The backend will handle schema creation automatically when `dotnet ef database update` is executed (see section 2.5). No manual SQL scripts are required for the pilot.

---

## 4. Azure Container Apps Deployment

This section covers the **one-time initial setup** of Azure Container Apps infrastructure using the Azure CLI. Subsequent deployments happen automatically via the `deploy.yml` GitHub Actions workflow.

### Prerequisites

- Azure CLI installed and authenticated: `az login`
- Azure subscription with the `Microsoft.App` and `Microsoft.OperationalInsights` resource providers registered
- Docker image already pushed to GHCR (or use a placeholder image for initial setup)
- GitHub repository secrets configured (see 4.6 below)

---

### 4.1 Create Resource Group

```bash
az group create \
  --name couplesync-rg \
  --location eastus
```

**Validation:** `az group show --name couplesync-rg` returns `"provisioningState": "Succeeded"`.

---

### 4.2 Create Container Apps Environment

The Container Apps Environment is the shared networking and observability boundary for your container apps. The Consumption plan is free up to 180,000 vCPU-seconds and 360,000 GiB-seconds per month.

```bash
az containerapp env create \
  --name couplesync-env \
  --resource-group couplesync-rg \
  --location eastus
```

**Validation:** `az containerapp env show --name couplesync-env --resource-group couplesync-rg` returns `"provisioningState": "Succeeded"`.

---

### 4.3 Create the Container App (First Deploy)

Run this command once to create the container app with all required secrets and environment variables. Replace the placeholder values with your actual secrets — **do not commit this command with real values**.

```bash
az containerapp create \
  --name couplesync-api \
  --resource-group couplesync-rg \
  --environment couplesync-env \
  --image "ghcr.io/<owner>/couplesync/api:sha-<initial-sha>" \
  --target-port 8080 \
  --ingress external \
  --min-replicas 0 \
  --max-replicas 1 \
  --secrets \
    "database-url=<DATABASE_URL value>" \
    "jwt-secret=<JWT__SECRET value>" \
    "firebase-credential-json=<FIREBASE_CREDENTIAL_JSON value>" \
    "azure-di-endpoint=<AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT value>" \
    "azure-di-key=<AZURE_DOCUMENT_INTELLIGENCE_KEY value>" \
    "firebase-storage-bucket=<FIREBASE_STORAGE_BUCKET value>" \
    "gemini-api-key=<GEMINI_API_KEY value>" \
    "gemini-model=gemini-2.0-flash" \
  --env-vars \
    "DATABASE_URL=secretref:database-url" \
    "JWT__SECRET=secretref:jwt-secret" \
    "FIREBASE_CREDENTIAL_JSON=secretref:firebase-credential-json" \
    "AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT=secretref:azure-di-endpoint" \
    "AZURE_DOCUMENT_INTELLIGENCE_KEY=secretref:azure-di-key" \
    "FIREBASE_STORAGE_BUCKET=secretref:firebase-storage-bucket" \
    "GEMINI_API_KEY=secretref:gemini-api-key" \
    "GEMINI_MODEL=secretref:gemini-model" \
    "JWT__ISSUER=CoupleSync" \
    "JWT__AUDIENCE=CoupleSync.Mobile" \
    "ASPNETCORE_ENVIRONMENT=Production"
```

**Security note:** Secrets are stored in Azure Container Apps secret store and never appear in environment variable values directly. All app secrets (`DATABASE_URL`, `JWT__SECRET`, `FIREBASE_CREDENTIAL_JSON`, `AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT`, `AZURE_DOCUMENT_INTELLIGENCE_KEY`, `FIREBASE_STORAGE_BUCKET`, `GEMINI_API_KEY`, `GEMINI_MODEL`) are referenced via `secretref:`. OCR, storage, and AI Chat secrets can be set to placeholder values initially and updated when those features are deployed.

**Liveness probe** is configured via the portal or the following update command after creation:

```bash
az containerapp update \
  --name couplesync-api \
  --resource-group couplesync-rg \
  --probe-type liveness \
  --probe-path /api/v1/health \
  --probe-port 8080 \
  --probe-interval 30 \
  --probe-timeout 5 \
  --probe-failure-threshold 3
```

**Validation:** `az containerapp show --name couplesync-api --resource-group couplesync-rg --query "properties.configuration.ingress.fqdn" --output tsv` returns a public FQDN. Then:

```bash
curl -f "https://<fqdn>/api/v1/health"
# Expected: {"status":"healthy"}
```

---

### 4.4 Update to a New Revision (Manual)

The `deploy.yml` workflow performs this step automatically on every push to `main`. To redeploy manually:

```bash
az containerapp update \
  --name couplesync-api \
  --resource-group couplesync-rg \
  --image "ghcr.io/<owner>/couplesync/api:sha-<new-sha>"
```

To update a secret value (e.g., rotate `JWT__SECRET`):

```bash
az containerapp secret set \
  --name couplesync-api \
  --resource-group couplesync-rg \
  --secrets "jwt-secret=<new-value>"

# A new revision must be activated after a secret rotation
az containerapp update \
  --name couplesync-api \
  --resource-group couplesync-rg \
  --image "ghcr.io/<owner>/couplesync/api:sha-<current-sha>"
```

---

### 4.5 Scale-to-Zero Behaviour

`--min-replicas 0` means the container app scales down to zero when idle, eliminating compute costs. The first request after a cold-start may take 5–15 seconds while the container starts. This is acceptable for a 10-user pilot.

To verify scale configuration:

```bash
az containerapp show \
  --name couplesync-api \
  --resource-group couplesync-rg \
  --query "properties.template.scale"
```

Expected output:

```json
{
  "minReplicas": 0,
  "maxReplicas": 1
}
```

---

### 4.6 GitHub Actions Secrets Required

The `deploy.yml` workflow reads the following GitHub repository secrets. Configure them under **Settings → Secrets and variables → Actions**:

| Secret | Purpose |
|--------|--------|
| `AZURE_CLIENT_ID` | Azure AD application (client) ID for OIDC federated login |
| `AZURE_TENANT_ID` | Azure AD tenant (directory) ID |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription ID containing `couplesync-rg` |

`DATABASE_URL`, `JWT__SECRET`, `FIREBASE_CREDENTIAL_JSON`, and all other app secrets are stored in ACA secrets (step 4.3) and are **not** needed as GitHub Secrets — the deploy workflow only pushes a new image and updates the revision pointer; secret values live in Azure.

### 4.7 One-time OIDC Federated Credential Setup

`deploy.yml` uses OIDC (no stored client secret) to authenticate with Azure. Perform this one-time setup:

```bash
# 1. Create or note the App Registration / Managed Identity object ID
APP_OBJECT_ID="<az ad app show --display-name couplesync-deploy --query id --output tsv>"

# 2. Add a federated credential for GitHub Actions push-to-main
az ad app federated-credential create \
  --id "${APP_OBJECT_ID}" \
  --parameters '{
    "name": "couplesync-github-main",
    "issuer": "https://token.actions.githubusercontent.com",
    "subject": "repo:<owner>/CoupleSync:ref:refs/heads/main",
    "audiences": ["api://AzureADTokenExchange"]
  }'

# 3. Grant Contributor on the resource group
az role assignment create \
  --assignee "<client-id>" \
  --role Contributor \
  --scope "/subscriptions/<subscription-id>/resourceGroups/couplesync-rg"
```

---

## 5. Android Build and Distribution

### 5.1 Prerequisites

- Expo CLI installed: `npm install -g expo-cli`
- EAS CLI installed: `npm install -g eas-cli` (if using cloud build)
- OR Android SDK + Gradle (for local builds)
- `mobile/google-services.json` in place (from section 1.3)
- Google Play Store developer account with app listing created

### 5.2 Prepare Mobile Project

```bash
cd mobile

# Install dependencies
npm install

# Or use Expo's package manager
expo install
```

### 5.3 Configure API Endpoint

Edit `mobile/.env.local` or update `mobile/app/_layout.tsx` to point to your backend API:

```typescript
// In mobile/src/services/apiClient.ts or app/_layout.tsx
const API_BASE_URL = process.env.EXPO_PUBLIC_API_BASE_URL || 'https://<your-backend-host>/api/v1';
```

Set the environment variable before building:
```bash
export EXPO_PUBLIC_API_BASE_URL="https://couplesync-backend.azurewebsites.net/api/v1"
# or on Windows PowerShell
$env:EXPO_PUBLIC_API_BASE_URL = "https://couplesync-backend.azurewebsites.net/api/v1"
```

**Validation:** Check that your backend is reachable and returning 400 (not 502/503):
```bash
curl -X POST https://<your-backend-host>/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","password":"test"}'
```

### 5.4 Build Android APK — Option A: Cloud Build (EAS)

Recommended for initial pilot distribution.

```bash
cd mobile

# Login to EAS (if not already logged in)
eas login

# Configure EAS for this project (one-time)
eas build:configure --platform android

# Start a build for the preview profile (creates APK for PlayInternal testing)
eas build --platform android --profile preview
```

**EAS Build Profiles:**
- `preview`: Creates an unsigned or debug-signed APK suitable for internal testing. File output: `.apk`
- `production`: Creates a release-signed APK suitable for Play Store submission. Requires keystore setup.

**Validation:** After the build completes (typically 10-20 minutes), you'll receive a download link for the APK. Download and verify:
```bash
ls -lh couplesync-app-preview.apk
```

### 5.5 Build Android APK — Option B: Local Build (Gradle)

Use this if you have Android SDK and Gradle locally.

```bash
cd mobile

# Install Android dependencies and generate AndroidManifest.xml
npx expo prebuild --platform android --clean

# Navigate to Android project and build
cd android
./gradlew assembleRelease

# APK output location
ls -la app/build/outputs/apk/release/app-release.apk
```

**Validation:** The APK file should exist and be > 50 MB (typical size with all dependencies).

### 5.6 Distribute to Google Play Internal Testing

1. **Go to Google Play Console** → Your app
2. **Navigate to Release channels** → **Internal testing** (or Setup → Internal testing track)
3. **Create new release:**
   - Upload APK file (from 4.4 or 4.5 above)
   - Add release notes (e.g., "Pilot build — testing core onboarding, notification capture, and FCM delivery")
4. **Review and release to internal testing**
5. **Add testers:** Invite internal team members via email address
6. **Testers receive:** Link to download from Google Play Store (testing version)

**Installation for Test Team:**
- Testers visit the Play Store release link
- Tap **Install** (or **Update** if they have an earlier build)
- Wait for APK download and installation
- Grant permissions when prompted (notification listener, etc., during onboarding)

**Validation:**
- Tester can open app on Android device
- Onboarding screens appear (login or register)
- No crash on startup

---

## Secret Files That Must Be Gitignored

The following files contain sensitive credentials and **must never be committed** to git:

| File | Purpose | Why Secret |
|------|---------|-----------|
| `mobile/google-services.json` | Firebase Android configuration | Contains API keys for Firebase (Cloud Messaging, Firestore, etc.) |
| `<firebase-admin-service-account>.json` | Backend Firebase Admin SDK credentials | Contains private key to send FCM messages — equivalent to database password |
| `.env` / `.env.local` | Local environment variables (if present) | May contain temporary secrets during development |

**How to Verify:**
```bash
# Check that these files are in .gitignore
cat .gitignore | grep -E "google-services|\.env"

# Verify no secret files are tracked
git status | grep -E "google-services|\.env"  # Should return nothing
```

If a secret file was accidentally committed, follow GitHub's [removing sensitive data guide](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).

---

## Environment Variables Reference

### Backend (.NET API)

| Variable | Required | Source | Example | Notes |
|----------|----------|--------|---------|-------|
| `JWT__SECRET` | Yes | Generate randomly | `a1b2c3d4e5f6g7h8i9j0k1l2m3n4o5p6` | Min 32 characters. Use `openssl rand -hex 16` |
| `JWT__ISSUER` | Yes | Fixed | `CoupleSync` | Must match appsettings.json default |
| `JWT__AUDIENCE` | Yes | Fixed | `CoupleSync.Mobile` | Must match appsettings.json default |
| `JWT__ACCESSTOKENTTLMINUTES` | No | Fixed | `15` | Defaults to 15 if not set |
| `JWT__REFRESHTOKENTTLDAYS` | No | Fixed | `7` | Defaults to 7 if not set |
| `DATABASE_URL` | Yes | PostgreSQL connection string | `Host=db.example.com;Port=5432;Database=couplesync;Username=couplesync_user;Password=secretpass123` | Connection to your PostgreSQL database |
| `FIREBASE_PROJECT_ID` | Yes | Firebase Console Project Settings | `couplesync-pilot` | Your Firebase project identifier |
| `FIREBASE_CREDENTIAL_JSON` | Yes | Service account JSON (base64 or raw) | `eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...` or raw JSON | Firebase Admin SDK credentials for FCM |

### Mobile (Android)

| Variable | Required | Source | Example | Notes |
|----------|----------|--------|---------|-------|
| `EXPO_PUBLIC_API_BASE_URL` | Yes (at build time) | Backend deployment URL | `https://couplesync-backend.azurewebsites.net/api/v1` | Public API endpoint for the backend. Must be set before build. |
| `EXPO_PUBLIC_ENV` | No | `development` or `production` | `pilot` | Optional: affects logging verbosity and error handling |
| `FIREBASE_PROJECT_ID` (via google-services.json) | Yes | Firebase Console | Embedded in `mobile/google-services.json` | Automatically read from google-services.json by Expo build |

---

## Validation Checklist

After deployment, run through the following validation steps:

### Backend API Validation

```bash
# 1. Health check — verify API is running
curl -X GET https://<your-backend-host>/health
# Expected: 200 OK (or 404 if health endpoint not implemented — that's OK)

# 2. Login/Register — verify auth is working
curl -X POST https://<your-backend-host>/api/v1/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email":"testpilot@couplesync.local",
    "name":"Test Pilot",
    "password":"TempPassword123!"
  }'
# Expected: 200 OK with user object and tokens

# 3. Database connectivity — verify migrations ran
# Connect to PostgreSQL and check tables exist
psql -h <your-db-host> -U <user> -d couplesync -c "\dt"
# Expected: List of tables (Couples, Users, Transactions, Goals, etc.)
```

### Mobile App Validation

1. **Device Preparation:**
   - Use an Android device (physical or emulator) running Android 10+
   - Ensure it has Google Play Services installed

2. **Installation:**
   - Download the APK from Google Play Internal Testing or via `eas build` link
   - Install on device

3. **First Launch:**
   - App should start without crashing
   - Welcome/onboarding screen should appear

4. **Onboarding Flow:**
   - Register a new account: enter email, name, password
   - Verify success: app stores auth tokens securely
   - Logout and login to verify refresh token works

5. **Couple Setup:**
   - Create a new couple (first partner)
   - Note the join code
   - In a second user account, use the join code to join the couple
   - Verify both users see the shared dashboard

6. **Notification Permission:**
   - Navigate to Settings → Notification Listener
   - Enable Android Notification Listener permission
   - Verify app appears in system notification access settings
   - (Do NOT send a real test notification yet — wait for backend alert policy testing)

7. **Dashboard:**
   - Backend should show an empty dashboard (no transactions yet)
   - No crashes when opening transactions, goals, or settings tabs

8. **Performance:**
   - Dashboard should load in under 2.5 seconds on a mid-tier Android device over stable network
   - Note load time for baseline tracking

### Firebase Validation

```bash
# 1. Firebase Console — verify project is active
# Visit https://console.firebase.google.com/project/<PROJECT_ID>/overview
# Should see green "Connected" status

# 2. FCM Credentials — verify backend can load them
# Check backend logs for errors like "FIREBASE_CREDENTIAL_JSON not set"
# If credentials are invalid, FCM dispatch will fail (visible in notification tests later)

# 3. Android App Registration — verify APK is linked
# In Firebase Console → Project Settings → Your Apps
# Should see "com.couplesync.app" listed
```

---

## Troubleshooting

### Backend API Not Starting

**Symptom:** API fails to start with error like `Unable to connect to database` or `FirebaseApp initialization failed`

**Solution:**
1. Verify `DATABASE_URL` is set and PostgreSQL is accessible: `psql -h <host> -U <user> -d couplesync -c "SELECT 1"`
2. Verify `FIREBASE_PROJECT_ID` is set and valid
3. Verify `FIREBASE_CREDENTIAL_JSON` is base64-decoded correctly (if using base64 encoding)
4. Check app logs for exact error message

### APK Installation Fails on Device

**Symptom:** Google Play says "Cannot install — app incompatible with your device"

**Solution:**
1. Verify device is Android 10+: **Settings** → **About phone** → **Android version**
2. Ensure device has Google Play Services installed: **Settings** → **Apps** → search for "Google Play Services"
3. If using emulator, ensure it has x86_64 or ARM64 image with Google Play Services included
4. Try clearing Play Store cache: **Settings** → **Apps** → **Google Play Store** → **Storage** → **Clear Cache**

### Notification Listener Permission Not Available

**Symptom:** In app settings, user cannot enable Notification Listener permission

**Solution:**
1. Verify the APK was built with the Expo Config Plugin for NotificationListenerService
2. Check AndroidManifest.xml for `<service>` entry: `grep NotificationListenerService android/app/src/main/AndroidManifest.xml`
3. If missing, rebuild APK with `npx expo prebuild --platform android --clean`

### Firebase Credentials Invalid

**Symptom:** Backend logs show `InvalidOperationException` or `FirebaseException` when sending notifications

**Solution:**
1. Verify the service account JSON is for the correct Firebase project
2. Ensure JSON was not modified or truncated during base64 encoding
3. If using raw JSON in environment variable, ensure no newlines or escape issues
4. Test Firebase credentials locally:
   ```csharp
   var credentialJson = Environment.GetEnvironmentVariable("FIREBASE_CREDENTIAL_JSON");
   var app = FirebaseApp.Create(new AppOptions { Credential = GoogleCredential.FromJson(credentialJson) });
   // Should not throw exception
   ```

---

## Rollback and Recovery

If something goes wrong post-deployment:

1. **API Crash:** Redeploy the last known-good published version from the `publish/` directory
2. **Database Migration Failure:** Use EF Core's migration rollback:
   ```bash
   dotnet ef database update <previous-migration-name>
   ```
3. **Android APK Issues:** Pull the previous APK version from Play Store Internal Testing and re-distribute to testers
4. **Firebase Credential Rotation:** Generate a new service account key and update `FIREBASE_CREDENTIAL_JSON` environment variable; restart API

---

## Next Steps After Successful Deployment

1. **Run End-to-End Tests:**
   - Follow the test scenarios in [T-029 E2E test guide](#) (when available)
   - Verify onboarding, notification capture, and FCM delivery work together

2. **Baseline Metrics:**
   - Record API response times for dashboard loads (target: < 2.5s)
   - Note notification delivery latency from backend dispatch to device receipt
   - Monitor error rates for transaction capture and categorization

3. **Pilot Onboarding:**
   - Invite 5 couples (10 users) to the internal testing track
   - Collect feedback on UX friction points
   - Monitor crash reports and error logs

4. **Ongoing Operations:**
   - Set up log aggregation (e.g., Azure Monitor, CloudWatch)
   - Configure alerting for API errors, database connectivity issues, and FCM failures
   - Schedule weekly sync meetings to review metrics and user feedback

---

## Support and Escalation

For deployment issues:
1. Check this runbook's **Troubleshooting** section
2. Review backend logs: `https://<your-api-host>/logs` (if exposed) or check hosting provider's log viewer
3. Contact the CoupleSync engineering team with:
   - Error message from logs
   - Timestamp of the issue
   - Steps taken so far

---

**End of Runbook**
