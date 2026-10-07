#!/usr/bin/env bash
# Builds the APK used ONLY by the "App E2E" workflow (.github/workflows/app-e2e.yml): the real app, release build,
# talking to a throwaway API on the machine that hosts the emulator.
#
# What makes this APK different from the published one — all of it done here, on the Android project that
# `expo prebuild` generates, never in mobile/app.json:
#   - EXPO_PUBLIC_API_BASE_URL is the host of the emulator (http://10.0.2.2:5000), over plain HTTP;
#   - android:usesCleartextTraffic="true" is added to <application> in the generated AndroidManifest.xml;
#   - expo-updates is switched off (expo.modules.updates.ENABLED=false, check on launch NEVER), so the APK runs
#     the JavaScript bundled from this checkout and never downloads a production OTA update over it;
#   - it is signed with the debug keystore of the generated project (no EAS, no EXPO_TOKEN, no
#     google-services.json, no production keystore).
# The finished APK is inspected (merged manifest and bundle) before it is accepted. It must never be published.
#
# Usage: scripts/app-e2e-build-apk.sh [output.apk]      (default: app-e2e-out/couplesync-e2e-test.apk)
#        Needs: `npm ci` already run in mobile/, JDK 17, the Android SDK (ANDROID_HOME or ANDROID_SDK_ROOT).
#        E2E_API_BASE_URL overrides the API address; only the emulator host or this machine is accepted.
# Run it in CI or in a throwaway copy of the repository: it creates mobile/android/.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOBILE="$ROOT/mobile"
OUT="${1:-$ROOT/app-e2e-out/couplesync-e2e-test.apk}"
API_BASE_URL="${E2E_API_BASE_URL:-http://10.0.2.2:5000}"
SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
MANIFEST="$MOBILE/android/app/src/main/AndroidManifest.xml"

fail() { echo "FAIL: $*" >&2; exit 1; }

# This build accepts plain HTTP: it may only ever point at the emulator host or at this machine.
case "$API_BASE_URL" in
  http://10.0.2.2:*|http://127.0.0.1:*|http://localhost:*) ;;
  *) fail "E2E_API_BASE_URL must be the emulator host (http://10.0.2.2:<port>) or this machine, got: $API_BASE_URL" ;;
esac
[ -n "$SDK" ] || fail "ANDROID_HOME (or ANDROID_SDK_ROOT) is not set"
[ -d "$MOBILE/node_modules" ] || fail "mobile/node_modules is missing: run 'npm ci' in mobile/ first"

# count_in_manifest <fixed text>: number of lines of the generated manifest that contain it.
count_in_manifest() { grep -cF -- "$1" "$MANIFEST" || true; }

# replace_once <label> <text that must be there exactly once> <sed expression>
replace_once() {
  local label="$1" before="$2" expression="$3"
  [ "$(count_in_manifest "$before")" = "1" ] || fail "$label: expected exactly one '$before' in the generated manifest"
  sed -i "$expression" "$MANIFEST"
}

cd "$MOBILE"

echo "== expo prebuild (android)"
# prebuild rewrites the "scripts" of package.json; the tracked file is put back so the build uses it as committed.
PACKAGE_JSON_BACKUP="$(mktemp)"
cp package.json "$PACKAGE_JSON_BACKUP"
rm -rf android
CI=1 EXPO_NO_TELEMETRY=1 npx expo prebuild --platform android --no-install
cp "$PACKAGE_JSON_BACKUP" package.json
rm -f "$PACKAGE_JSON_BACKUP"
[ -f "$MANIFEST" ] || fail "prebuild did not generate $MANIFEST"

echo "== test-only changes to the generated AndroidManifest.xml"
[ "$(count_in_manifest 'android:usesCleartextTraffic')" = "0" ] || fail "the generated manifest already sets usesCleartextTraffic"
replace_once "cleartext HTTP" '<application ' \
  's|<application |<application android:usesCleartextTraffic="true" |'
replace_once "expo-updates off" 'android:name="expo.modules.updates.ENABLED" android:value="true"' \
  's|\(android:name="expo.modules.updates.ENABLED" android:value="\)true"|\1false"|'
replace_once "expo-updates check on launch" 'android:name="expo.modules.updates.EXPO_UPDATES_CHECK_ON_LAUNCH"' \
  's|\(android:name="expo.modules.updates.EXPO_UPDATES_CHECK_ON_LAUNCH" android:value="\)[A-Z_]*"|\1NEVER"|'

[ "$(count_in_manifest '<application android:usesCleartextTraffic="true" ')" = "1" ] || fail "usesCleartextTraffic was not applied"
[ "$(count_in_manifest 'android:name="expo.modules.updates.ENABLED" android:value="false"')" = "1" ] || fail "expo-updates was not switched off"
[ "$(count_in_manifest 'android:name="expo.modules.updates.EXPO_UPDATES_CHECK_ON_LAUNCH" android:value="NEVER"')" = "1" ] || fail "check on launch was not set to NEVER"
grep -F -e 'usesCleartextTraffic' -e 'expo.modules.updates' "$MANIFEST" | sed 's/^ */  /' | cut -c 1-200

echo "== gradle assembleRelease (x86_64 only, API at $API_BASE_URL)"
cd "$MOBILE/android"
EXPO_PUBLIC_API_BASE_URL="$API_BASE_URL" NODE_ENV=production CI=1 EXPO_NO_TELEMETRY=1 \
  ./gradlew :app:assembleRelease -PreactNativeArchitectures=x86_64 --no-daemon --console=plain
APK="$MOBILE/android/app/build/outputs/apk/release/app-release.apk"
[ -f "$APK" ] || fail "the build did not produce $APK"

echo "== checking the APK that was built"
AAPT2="$(find "$SDK/build-tools" -maxdepth 2 -name aapt2 -type f 2>/dev/null | sort -V | tail -n 1)"
[ -n "$AAPT2" ] || fail "aapt2 not found under $SDK/build-tools"
TREE="$("$AAPT2" dump xmltree --file AndroidManifest.xml "$APK")"

# value_after <android:name of a meta-data>: the android:value line that follows it in the merged manifest.
value_after() { echo "$TREE" | grep -F -A 1 "=\"$1\"" | grep -F ':value(' || true; }

package_line="$(echo "$TREE" | grep -m 1 -F ' package=' || true)"
cleartext_line="$(echo "$TREE" | grep -F ':usesCleartextTraffic(' || true)"
updates_enabled_line="$(value_after expo.modules.updates.ENABLED)"
check_on_launch_line="$(value_after expo.modules.updates.EXPO_UPDATES_CHECK_ON_LAUNCH)"
echo "  $package_line"
echo "  $cleartext_line"
echo "  expo.modules.updates.ENABLED -> $updates_enabled_line"
echo "  expo.modules.updates.EXPO_UPDATES_CHECK_ON_LAUNCH -> $check_on_launch_line"
case "$package_line" in *'package="com.couplesync.app"'*) ;; *) fail "unexpected package in the APK" ;; esac
case "$cleartext_line" in *'=true'*|*'0xffffffff'*) ;; *) fail "the APK does not allow cleartext HTTP" ;; esac
case "$updates_enabled_line" in *'=false'*|*')0x0'*|*'="false"'*) ;; *) fail "expo-updates is not switched off in the APK" ;; esac
case "$check_on_launch_line" in *'NEVER'*) ;; *) fail "the APK still checks for updates on launch" ;; esac

# The address is compiled into the JavaScript bundle. It must be the test one, and never the production API.
BUNDLE="$(mktemp)"
unzip -p "$APK" assets/index.android.bundle > "$BUNDLE"
[ -s "$BUNDLE" ] || fail "assets/index.android.bundle is missing from the APK"
api_host="${API_BASE_URL#http://}"
grep -aqF "$api_host" "$BUNDLE" || fail "the bundle does not contain the test API address ($api_host)"
if grep -aqF 'onrender.com' "$BUNDLE"; then
  rm -f "$BUNDLE"
  fail "the bundle contains the production API address"
fi
rm -f "$BUNDLE"
echo "  bundle: contains $api_host, does not contain the production API address"

mkdir -p "$(dirname "$OUT")"
cp "$APK" "$OUT"
echo "TEST APK READY: $OUT ($(du -h "$OUT" | cut -f 1))"
