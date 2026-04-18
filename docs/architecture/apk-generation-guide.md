# APK Generation Guide — CoupleSync V1.5

> Scope: Producing installable Android APK builds for pilot testing.  
> Two paths: **EAS Build** (recommended, cloud-based) and **local Gradle** (fallback).

---

## Prerequisites

| Tool | Required version | Install |
|---|---|---|
| Node.js | 18 LTS or 20 LTS | https://nodejs.org |
| Expo CLI | `npx expo` (bundled) | `npm install -g expo-cli` |
| EAS CLI | Latest | `npm install -g eas-cli` |
| Expo account | Free | https://expo.dev/signup |
| Firebase project | Existing CoupleSync project | Firebase Console |

---

## Path 1 — EAS Build (Recommended)

EAS Build is Expo's cloud build service. The `preview` profile produces a directly-installable APK without Play Store submission. Free tier: **30 builds/month**.

### Step 1 — Log in to Expo

```bash
eas login
# Enter your Expo account credentials
```

### Step 2 — Configure `eas.json`

Create `mobile/eas.json` with the following content:

```json
{
  "cli": {
    "version": ">= 12.0.0",
    "appVersionSource": "remote"
  },
  "build": {
    "development": {
      "developmentClient": true,
      "distribution": "internal",
      "android": {
        "buildType": "apk"
      }
    },
    "preview": {
      "distribution": "internal",
      "android": {
        "buildType": "apk"
      },
      "channel": "preview"
    },
    "production": {
      "android": {
        "buildType": "app-bundle"
      },
      "channel": "production"
    }
  },
  "submit": {
    "production": {}
  }
}
```

**Profile notes**:
- `preview` — produces an unsigned APK installable on any device with "Unknown sources" enabled. Used for pilot distribution.
- `production` — produces an AAB for Play Store (out of scope for V1.5 pilot).
- `development` — produces an APK with Expo Dev Client for development testing.

### Step 3 — Configure `app.json`

Ensure `mobile/app.json` has the correct Android package name and versioning:

```json
{
  "expo": {
    "name": "CoupleSync",
    "slug": "couplesync",
    "version": "1.5.0",
    "android": {
      "package": "com.couplesync.app",
      "versionCode": 2,
      "googleServicesFile": "./google-services.json",
      "permissions": [
        "android.permission.BIND_NOTIFICATION_LISTENER_SERVICE",
        "android.permission.RECEIVE_BOOT_COMPLETED",
        "android.permission.VIBRATE"
      ]
    },
    "extra": {
      "eas": {
        "projectId": "<your-expo-project-id>"
      }
    }
  }
}
```

Get `projectId` from the EAS dashboard after Step 4 or by running `eas project:init`.

### Step 4 — Initialize the EAS project

```bash
cd mobile
eas project:init
# This links the local project to your Expo account and writes the projectId to app.json
```

### Step 5 — Trigger the APK build

```bash
cd mobile
eas build --platform android --profile preview --non-interactive
```

EAS Build will:
1. Bundle JavaScript and assets.
2. Download Android NDK, Gradle, and compile native modules on EAS cloud workers.
3. Sign the APK with a managed keystore (EAS manages this automatically on first build).
4. Upload the APK to the EAS build artifact storage.

**Estimated build time**: 8–15 minutes.

### Step 6 — Download the APK

After build completion, the EAS CLI prints a download URL. Alternatively:

```bash
eas build:list --platform android --limit 1
# Copy the artifact URL and download
```

Or visit https://expo.dev/accounts/[account]/projects/couplesync/builds.

### Step 7 — Install on device

1. Transfer the APK to an Android device (USB, email, or direct download link).
2. On the device: **Settings → Security → Install unknown apps → Allow from [source]**.
3. Tap the `.apk` file to install.

---

## Path 2 — Firebase App Distribution

Firebase App Distribution allows distributing APKs to a tester group via email invitation. Testers receive a link and install via the Firebase App Tester companion app or direct APK download.

### Step 1 — Create a tester group in Firebase Console

1. Open [Firebase Console](https://console.firebase.google.com) → your CoupleSync project.
2. Navigate to **App Distribution** → **Testers & Groups**.
3. Create a group `pilot-testers`.
4. Add pilot testers by email.

### Step 2 — Install Firebase CLI

```bash
npm install -g firebase-tools
firebase login
```

### Step 3 — Upload APK to App Distribution

```bash
firebase appdistribution:distribute path/to/couplesync-preview.apk \
  --app <FIREBASE_APP_ID> \
  --groups "pilot-testers" \
  --release-notes "V1.5 preview build — cloud deployment + budget management"
```

`FIREBASE_APP_ID` is found in Firebase Console → Project Settings → Your Apps → App ID (format: `1:123456789:android:abc123`).

### Step 4 — Tester experience

1. Tester receives email from Firebase with a link to install the Firebase App Tester app (one-time).
2. On subsequent releases, tester opens Firebase App Tester → CoupleSync → Download → Install.

---

## GitHub Actions — Automated APK Build and Distribution

Add `.github/workflows/mobile-apk.yml` to automate builds on pushes to `main` or manual dispatch:

```yaml
name: Mobile APK Build

on:
  workflow_dispatch:
  push:
    tags:
      - 'v*'

jobs:
  build-apk:
    name: EAS Build APK
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: mobile

    steps:
      - uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: mobile/package-lock.json

      - name: Install dependencies
        run: npm ci

      - name: TypeScript check
        run: npx tsc --noEmit

      - name: Install EAS CLI
        run: npm install -g eas-cli

      - name: Build APK (preview)
        run: eas build --platform android --profile preview --non-interactive
        env:
          EXPO_TOKEN: ${{ secrets.EXPO_TOKEN }}

      - name: Download APK artifact
        run: |
          BUILD_URL=$(eas build:list --platform android --limit 1 --json | jq -r '.[0].artifacts.buildUrl')
          curl -L "$BUILD_URL" -o couplesync-preview.apk
        env:
          EXPO_TOKEN: ${{ secrets.EXPO_TOKEN }}

      - name: Upload to Firebase App Distribution
        run: |
          npm install -g firebase-tools
          firebase appdistribution:distribute couplesync-preview.apk \
            --app "${{ secrets.FIREBASE_APP_ID }}" \
            --groups "pilot-testers" \
            --release-notes "Build from ${{ github.ref_name }}"
        env:
          FIREBASE_TOKEN: ${{ secrets.FIREBASE_TOKEN }}
```

**Secrets required in GitHub repository**:
| Secret | Value |
|---|---|
| `EXPO_TOKEN` | EAS access token (`eas token:create`) |
| `FIREBASE_APP_ID` | Firebase Android App ID |
| `FIREBASE_TOKEN` | Firebase CI token (`firebase login:ci`) |

---

## Path 3 — Local Gradle Build (Fallback)

Use this path when EAS Build quota is exhausted or CI is unavailable.

### Prerequisites

- Android Studio installed (includes Android SDK, Gradle, JDK 17).
- `ANDROID_HOME` environment variable set to SDK path.

### Step 1 — Generate native Android project

```bash
cd mobile
npx expo prebuild --platform android --clean
```

This generates the `android/` native project directory from the Expo config.

### Step 2 — Build debug APK

```bash
cd mobile/android
./gradlew assembleRelease
```

APK output: `mobile/android/app/build/outputs/apk/release/app-release.apk`

**Note**: The release build requires a signing keystore. For pilot purposes, use debug signing:

```bash
./gradlew assembleDebug
# Output: mobile/android/app/build/outputs/apk/debug/app-debug.apk
```

### Step 3 — Install via ADB

```bash
adb install mobile/android/app/build/outputs/apk/debug/app-debug.apk
```

Or transfer the APK manually to the device.

---

## Troubleshooting

| Issue | Cause | Fix |
|---|---|---|
| `eas build` fails with "Missing credentials" | Keystore not set up | Run `eas credentials` to let EAS manage keystore automatically |
| Build fails: `google-services.json not found` | Missing Firebase config | Copy `google-services.json` from Firebase Console → Project Settings → Android app → Download |
| APK installs but crashes on launch | API URL not set for production | Ensure `EXPO_PUBLIC_API_URL` env var is set in `eas.json` or `.env.preview` |
| Firebase App Distribution upload fails | Firebase token expired | Re-run `firebase login:ci` and update `FIREBASE_TOKEN` secret |
| EAS Build quota exhausted (30/mo) | Too many builds | Use local Gradle build (Path 3) for remainder of month |
| App installs but shows "Install blocked" | Unknown sources disabled | Device → Settings → Security → Install unknown apps → Allow |

---

## Environment Variables for Mobile Builds

Create `mobile/.env.preview` (not committed — add to `.gitignore`):

```bash
EXPO_PUBLIC_API_URL=https://<your-app>.azurecontainerapps.io/api/v1
EXPO_PUBLIC_FIREBASE_MESSAGING_SENDER_ID=<from google-services.json>
```

Reference in `mobile/app.json` under `extra`:

```json
{
  "expo": {
    "extra": {
      "apiUrl": "https://<your-app>.azurecontainerapps.io/api/v1"
    }
  }
}
```

Access in code:

```ts
import Constants from 'expo-constants';
const API_URL = Constants.expoConfig?.extra?.apiUrl ?? 'http://localhost:8080/api/v1';
```

---

## Version Management

- Bump `version` in `app.json` for each user-facing release.
- Increment `android.versionCode` by 1 for every build submitted to Firebase App Distribution (even test builds); Firebase App Distribution requires unique version codes.
- EAS auto-manages `versionCode` when `"appVersionSource": "remote"` is set in `eas.json`.
