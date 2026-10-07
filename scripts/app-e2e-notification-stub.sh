#!/usr/bin/env bash
# Builds the notification test double of the "App E2E" workflow: a tiny APK (one activity, no screen, no
# resources) that posts one made-up bank notification. Sources and the reason it exists:
# mobile/tests/e2e/notification-stub/. It is installed on the throwaway emulator only and is never published.
#
# No Gradle: javac + d8 + aapt2 + zipalign + apksigner from the Android SDK, signed with a key generated here
# and thrown away.
#
# Usage: scripts/app-e2e-notification-stub.sh [output.apk]   (default: app-e2e-out/notification-stub.apk)
#        Needs a JDK (javac, keytool) and the Android SDK (ANDROID_HOME or ANDROID_SDK_ROOT) with one
#        platform and one build-tools version installed.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/mobile/tests/e2e/notification-stub"
OUT="${1:-$ROOT/app-e2e-out/notification-stub.apk}"
SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"

fail() { echo "FAIL: $*" >&2; exit 1; }

[ -n "$SDK" ] || fail "ANDROID_HOME (or ANDROID_SDK_ROOT) is not set"
BUILD_TOOLS="$(find "$SDK/build-tools" -mindepth 1 -maxdepth 1 -type d 2>/dev/null | sort -V | tail -n 1)"
PLATFORM_JAR="$(find "$SDK/platforms" -mindepth 2 -maxdepth 2 -name android.jar 2>/dev/null | sort -V | tail -n 1)"
[ -n "$BUILD_TOOLS" ] || fail "no build-tools under $SDK/build-tools"
[ -n "$PLATFORM_JAR" ] || fail "no android.jar under $SDK/platforms"
echo "== build-tools: $BUILD_TOOLS"
echo "== platform:    $PLATFORM_JAR"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/classes" "$WORK/dex"

javac --release 11 -classpath "$PLATFORM_JAR" -d "$WORK/classes" \
  "$SOURCE/src/com/couplesync/e2estub/PostNotificationActivity.java"
"$BUILD_TOOLS/d8" --lib "$PLATFORM_JAR" --min-api 26 --output "$WORK/dex" \
  "$WORK/classes/com/couplesync/e2estub/PostNotificationActivity.class"
"$BUILD_TOOLS/aapt2" link --manifest "$SOURCE/AndroidManifest.xml" -I "$PLATFORM_JAR" -o "$WORK/unsigned.apk"
# aapt (version 1) only adds the compiled code to the archive; it stores the path as given, hence the cd.
(cd "$WORK/dex" && "$BUILD_TOOLS/aapt" add "$WORK/unsigned.apk" classes.dex >/dev/null)
"$BUILD_TOOLS/zipalign" -f 4 "$WORK/unsigned.apk" "$WORK/aligned.apk"

keytool -genkeypair -keystore "$WORK/stub.keystore" -storepass android -keypass android -alias stub \
  -keyalg RSA -keysize 2048 -validity 2 -dname "CN=CoupleSync E2E notification stub" >/dev/null 2>&1
mkdir -p "$(dirname "$OUT")"
"$BUILD_TOOLS/apksigner" sign --ks "$WORK/stub.keystore" --ks-pass pass:android --key-pass pass:android \
  --out "$OUT" "$WORK/aligned.apk"
"$BUILD_TOOLS/apksigner" verify "$OUT"
echo "NOTIFICATION STUB READY: $OUT"
