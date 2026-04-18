# CoupleSync Mobile App

An Expo-based React Native Android app for couples' budgeting and financial planning. Captures spending data from bank push notifications, tracks shared expenses, sets savings goals, and forecasts cash flow together.

## Features

- **Register/Login** with secure JWT-based authentication
- **Couple onboarding** — one user creates a couple, the other joins via invite code
- **Bank notification capture** — Android NotificationListenerService reads bank push notifications, parses them locally, and sends structured transaction data to the backend
- **Transaction dashboard** — view shared expenses, individual balances, and transaction history
- **Savings goals** — create, track, and archive shared financial goals
- **30 & 90-day cash flow projections** — forecast combined spending and balance trends
- **Push alerts** — receive notifications for low balance, large transactions, and upcoming bills
- **Offline resilience** — queued uploads and retry logic for network interruptions

## Requirements

- **Node.js 18+** and npm
- **Expo CLI:** `npm install -g expo-cli`
- **Android device or emulator** (Android 10+)
- **.NET backend** deployed and reachable at your `EXPO_PUBLIC_API_BASE_URL` (see Configuration below)
- **Firebase project** with `google-services.json` placed in `mobile/` (see Deployment Runbook)

## Quick Start

### 1. Clone and Install

```bash
git clone https://github.com/lucasmoraiss/CoupleSync.git
cd CoupleSync/mobile

npm install
# or
expo install
```

### 2. Configure API Endpoint

Set the backend API URL as an environment variable before running or building:

```bash
# Linux/macOS
export EXPO_PUBLIC_API_BASE_URL="http://localhost:5000/api/v1"

# Windows PowerShell
$env:EXPO_PUBLIC_API_BASE_URL = "http://localhost:5000/api/v1"
```

For production, replace `localhost:5000` with your deployed backend URL (e.g., `https://couplesync-backend.azurewebsites.net/api/v1`).

### 3. Start Development Server

```bash
npx expo start --android

# Or start and immediately launch on connected device/emulator
npx expo start --android --clear

# Press 'a' to open on emulator or attached device
```

The Expo dev server will start on `http://localhost:8081`. Scan the QR code with Expo Go app or use a direct connection.

### 4. Test the App

1. **Onboarding:**
   - Tap **Register** on the splash screen
   - Enter email, name, and password
   - Create a couple or join via code

2. **Dashboard:**
   - View shared net worth, transaction history, and partner breakdown
   - Should load in < 2.5s

3. **Notification Listener Permission:**
   - Go to **Settings** tab
   - Tap **Enable Notification Listener**
   - Approve Android permission
   - **Do not test with real bank notifications yet** (see Bank Integration section)

4. **Goals & Projections:**
   - **Goals tab** — create a savings goal
   - **Cash Flow tab** — view 30 and 90-day projections

### 5. Run Tests (if available)

```bash
npm test
# or
npx jest
```

## Project Structure

```
mobile/
├── app/                                  # Expo Router file-based routing
│   ├── _layout.tsx                       # Root layout and navigation setup
│   ├── (auth)/                           # Auth stack (login, register)
│   │   ├── login.tsx
│   │   ├── register.tsx
│   │   └── couple-setup.tsx
│   └── (main)/                           # Main tab navigation
│       ├── index.tsx                     # Dashboard tab
│       ├── cashflow/index.tsx            # Cash flow projections
│       ├── goals/index.tsx               # Goals CRUD
│       ├── transactions/index.tsx        # Transaction history
│       ├── settings/                     # Settings screens
│       └── _layout.tsx                   # Tab navigation
├── src/
│   ├── modules/
│   │   ├── auth/                         # Auth logic and hooks
│   │   ├── couple/                       # Couple creation and joining
│   │   ├── integrations/notification-capture/
│   │   │   ├── NotificationListenerBridge.ts   # React Native bridge
│   │   │   ├── notificationParser.ts           # Bank pattern regex parser
│   │   │   ├── eventUploader.ts               # Queued upload to backend
│   │   │   └── notification-patterns.json     # Bank-specific patterns
│   │   ├── dashboard/                   # Dashboard aggregation
│   │   ├── goals/                       # Goal CRUD operations
│   │   └── cashflow/                    # Projection data fetching
│   ├── services/
│   │   ├── apiClient.ts                 # Typed HTTP client (axios/fetch)
│   │   └── pushTokenService.ts          # FCM device token registration
│   ├── state/
│   │   └── sessionStore.ts              # Zustand store for auth/user state
│   ├── types/
│   │   └── api.ts                       # TypeScript interfaces for backend contracts
│   └── components/
│       └── ui/                          # Reusable UI components
├── plugins/
│   └── withNotificationListener.ts      # Expo Config Plugin: injects NotificationListenerService into AndroidManifest
├── android/                             # Native Android code (generated by expo prebuild)
│   └── app/src/main/java/com/couplesync/app/
│       ├── NotificationCaptureService.kt       # NotificationListenerService implementation
│       ├── NotificationBridgeModule.kt         # React Native bridge module
│       └── NotificationBridgePackage.kt        # Module registration
├── app.json                             # Expo app configuration (references withNotificationListener plugin)
├── package.json                         # Dependencies
└── tsconfig.json                        # TypeScript configuration
```

## Environment Variables

| Variable | Required | Example | Notes |
|----------|----------|---------|-------|
| `EXPO_PUBLIC_API_BASE_URL` | Yes | `http://localhost:5000/api/v1` | Backend API endpoint. MUST be set before starting dev server or building APK. Public variables must be prefixed with `EXPO_PUBLIC_` to be baked into the APK. |
| `EXPO_PUBLIC_ENV` | No | `development` | For future feature flags or debug logging. |

## Google Services Configuration

**IMPORTANT:** The `mobile/google-services.json` file must be present at build time but **must NOT be committed to git** for security reasons.

### Setup (One-Time)

1. Complete the Firebase project setup in the [Deployment Runbook](../docs/deployment/pilot-runbook.md#firebase-project-setup)
2. Download `google-services.json` from Firebase Console
3. Place it at `mobile/google-services.json`
4. Verify `.gitignore` includes: `google-services.json`

### For Contributors

If you have a clean clone without `google-services.json`:

```bash
# Ask a team member for the file or regenerate it from Firebase Console
# It should be placed at mobile/google-services.json before building
```

## Building for Distribution

### Option 1: Cloud Build with EAS (Recommended)

```bash
# Install EAS CLI
npm install -g eas-cli

# Login to EAS account (if not already)
eas login

# Build for internal testing
eas build --platform android --profile preview

# Or build for release
eas build --platform android --profile production
```

The APK download link will be provided. Upload it to Google Play Console's **Internal Testing** track.

### Option 2: Local Build with Gradle

```bash
# Generate Android project with all plugins
npx expo prebuild --platform android --clean

# Navigate to Android project
cd android

# Build release APK
./gradlew assembleRelease

# APK output
ls app/build/outputs/apk/release/app-release.apk
```

Upload the APK to Google Play Console.

## Behind the Scenes: Bank Notification Capture

When you enable the Notification Listener permission:

1. **Android NotificationListenerService** (Kotlin) receives bank push notifications
2. **notificationParser.ts** extracts transaction details (amount, merchant, description) using regex patterns per bank
3. **eventUploader.ts** queues the structured event and sends to `POST /api/v1/integrations/events`
4. **Backend** validates, deduplicates, and creates a Transaction record
5. **Dashboard** refreshes to show the new transaction

See `mobile/src/modules/integrations/notification-capture/notification-patterns.json` for supported banks.

## Troubleshooting

### App Won't Build — Missing google-services.json

```
error: firebase-config-file not found
```

**Solution:**
1. Download `google-services.json` from Firebase Console (Project Settings → Your Apps → Android)
2. Place at `mobile/google-services.json`
3. Run `npx expo prebuild --platform android --clean` again

### API Connection Fails

**Symptom:** Login/register screens show "Network error" or "Cannot reach server"

**Solution:**
1. Verify backend is running and accessible:
   ```bash
   curl http://localhost:5000/api/v1/auth/login  # Should return 400 (OK, just no data)
   ```
2. Verify `EXPO_PUBLIC_API_BASE_URL` is set correctly and exported before running app:
   ```bash
   echo $EXPO_PUBLIC_API_BASE_URL  # Should print your backend URL
   ```
3. If using localhost, ensure emulator/device can reach your dev machine (often need `10.0.2.2` instead of `localhost` on Android emulator)

### Notification Listener Permission Denied

**Symptom:** Settings screen shows permission as disabled even after granting

**Solution:**
1. Verify `app.json` includes the `./plugins/withNotificationListener` plugin
2. Run `npx expo prebuild --platform android --clean` to regenerate AndroidManifest
3. On device, go **Settings** → **Apps & notifications** → **CoupleSync** → **Permissions** → **Notifications** → enable
4. Restart the app

### Transactions Not Uploading

**Symptom:** Notification listener is enabled, but captured transactions don't appear in backend

**Solution:**
1. Verify backend is reachable (see API Connection Fails above)
2. Check `eventUploader.ts` logs — if queued uploads repeatedly fail, inspect:
   - Network connectivity (`adb logcat | grep CoupleSync`)
   - Backend `/api/v1/integrations/events` endpoint response
   - JWT token validity (refresh if expired)

## Performance Targets

- **Dashboard load:** < 2.5 seconds (mid-tier Android, stable network)
- **Transaction upload:** < 60 seconds from notification receipt to backend persistence
- **Goal/projection loads:** < 1 second
- **App startup:** < 2 seconds on device with app already installed

## Contributing

1. Create a feature branch: `git checkout -b feature/your-feature`
2. Follow the project structure and TypeScript conventions
3. Test on a real Android device (emulator may differ)
4. Commit with a clear message: `git commit -am "feat: add your feature"`
5. Push and create a Pull Request

## Full Deployment Guide

For complete step-by-step instructions to deploy the mobile app to testers via Google Play Internal Testing, including:
- Firebase project setup
- Backend deployment
- APK building and distribution
- Validation checklist

See [docs/deployment/pilot-runbook.md](../docs/deployment/pilot-runbook.md).

## License

CoupleSync is proprietary software. See LICENSE file for details.
