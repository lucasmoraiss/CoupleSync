#!/usr/bin/env bash
# Runs the Maestro screen tests of the "App E2E" workflow on an Android emulator that is ALREADY running, with
# the test API already answering on this machine (scripts/app-e2e-api.sh start).
#
# - installs the test APK (scripts/app-e2e-build-apk.sh) and the notification test double
#   (scripts/app-e2e-notification-stub.sh);
# - runs every flow of mobile/tests/e2e/flows/ in order; a flow that fails is repeated ONCE, and a flow that
#   only passed on the repetition is reported as such in the summary;
# - drives flow 07 itself, because Maestro cannot run adb: grant the notification access, run the consent part,
#   post the made-up bank notification, run the part that checks the transaction;
# - keeps the evidence (Maestro logs and screenshots, logcat) under the evidence directory;
# - writes a table to the job summary and exits 1 when a required flow failed.
#
# Usage: scripts/app-e2e-run-flows.sh
#        E2E_APK, E2E_STUB_APK, E2E_EVIDENCE_DIR and E2E_API_PORT override the defaults below.
#        Needs adb and maestro on PATH and exactly one device (or ANDROID_SERIAL set).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FLOWS="$ROOT/mobile/tests/e2e/flows"
APK="${E2E_APK:-$ROOT/app-e2e-out/couplesync-e2e-test.apk}"
STUB_APK="${E2E_STUB_APK:-$ROOT/app-e2e-out/notification-stub.apk}"
EVIDENCE="${E2E_EVIDENCE_DIR:-$ROOT/app-e2e-out/evidence}"
API_BASE="http://127.0.0.1:${E2E_API_PORT:-5000}"

APP_ID="com.couplesync.app"
# The listener declared by mobile/plugins/withNotificationListener.js (class in mobile/android-native/).
LISTENER="com.couplesync.app/com.couplesync.app.NotificationCaptureService"
# The test double (mobile/tests/e2e/notification-stub/): its package is one the listener accepts.
STUB_ACTIVITY="com.nu.production/com.couplesync.e2estub.PostNotificationActivity"
CAPTURE_FLOW="07-captura-de-notificacao"

FLOW_NAMES=(
  "01-cadastro-e-grupo"
  "02-login-e-logout"
  "03-entrar-em-grupo"
  "04-transacao-manual"
  "05-metas-e-rendas"
  "06-abas"
  "$CAPTURE_FLOW"
)
# Flows listed here are reported but do not fail the job. Empty on purpose: every flow is required.
INFORMATIONAL_FLOWS=()
# One Maestro run may not hang the job: generous, far above a normal flow (1 to 3 minutes).
FLOW_TIMEOUT_SECONDS=900

export MAESTRO_CLI_NO_ANALYTICS=1
export MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED=true
export MAESTRO_DRIVER_STARTUP_TIMEOUT="${MAESTRO_DRIVER_STARTUP_TIMEOUT:-180000}"

fail() { echo "FAIL: $*" >&2; exit 1; }

is_informational() {
  local name
  for name in "${INFORMATIONAL_FLOWS[@]:-}"; do
    [ "$name" = "$1" ] && return 0
  done
  return 1
}

# maestro_run <label> <flow file>: one Maestro run; its log, report and screenshots go to the evidence directory.
maestro_run() {
  local label="$1" file="$2" status
  echo "--- maestro: $label"
  timeout "$FLOW_TIMEOUT_SECONDS" maestro test \
    --format junit --output "$EVIDENCE/maestro/$label.xml" \
    --debug-output "$EVIDENCE/maestro/$label" --flatten-debug-output \
    "$file" 2>&1 | tee "$EVIDENCE/maestro/$label.log"
  status="${PIPESTATUS[0]}"
  if [ "$status" != "0" ]; then
    # What was on the screen when it stopped, whatever Maestro itself managed to save.
    adb exec-out screencap -p > "$EVIDENCE/screens/$label.png" 2>/dev/null || true
  fi
  return "$status"
}

# The capture flow: steps 1 to 4 described in 07-captura-de-notificacao.yaml.
run_capture_flow() {
  local label="$1" status=0
  adb shell pm clear "$APP_ID" >/dev/null || return 1
  adb shell cmd notification allow_listener "$LISTENER" || return 1
  if maestro_run "$label-consentimento" "$FLOWS/$CAPTURE_FLOW.yaml"; then
    # The made-up notification, posted by the test double as a bank app would.
    adb shell am start -W -n "$STUB_ACTIVITY" || status=1
    if [ "$status" = "0" ]; then
      maestro_run "$label-transacao" "$FLOWS/partes/07-conferir-transacao-capturada.yaml" || status=1
    fi
  else
    status=1
  fi
  # The access is taken back so that it never leaks into another flow (they must not see the consent screen).
  adb shell cmd notification disallow_listener "$LISTENER" || true
  return "$status"
}

run_flow() {
  local name="$1" attempt="$2"
  if [ "$name" = "$CAPTURE_FLOW" ]; then
    run_capture_flow "$name-tentativa$attempt"
  else
    maestro_run "$name-tentativa$attempt" "$FLOWS/$name.yaml"
  fi
}

command -v adb >/dev/null || fail "adb is not on PATH"
command -v maestro >/dev/null || fail "maestro is not on PATH"
[ -f "$APK" ] || fail "test APK not found: $APK"
[ -f "$STUB_APK" ] || fail "notification test double not found: $STUB_APK"
[ "$(curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' "$API_BASE/health/ready" || true)" = "200" ] \
  || fail "the test API is not answering at $API_BASE/health/ready"

mkdir -p "$EVIDENCE/maestro" "$EVIDENCE/screens"
maestro --version || fail "maestro does not start"

echo "== installing the test APK and the notification test double"
adb wait-for-device
adb install -r "$APK" || fail "could not install the test APK"
# -g: the test double may post notifications without anyone answering the permission dialog.
adb install -r -g "$STUB_APK" || fail "could not install the notification test double"
adb shell cmd notification disallow_listener "$LISTENER" >/dev/null 2>&1 || true

adb logcat -c || true
adb logcat -v time > "$EVIDENCE/logcat.txt" 2>&1 &
LOGCAT_PID=$!
trap 'kill "$LOGCAT_PID" >/dev/null 2>&1 || true' EXIT

RESULTS=()
failed_required=0
for name in "${FLOW_NAMES[@]}"; do
  echo "== flow $name"
  if run_flow "$name" 1; then
    result="passou"
  else
    echo "== flow $name failed: repeating once"
    if run_flow "$name" 2; then
      result="passou só na repetição"
    elif is_informational "$name"; then
      result="FALHOU (informativo: não derruba o job)"
    else
      result="FALHOU"
      failed_required=1
    fi
  fi
  echo "== flow $name: $result"
  RESULTS+=("$name|$result")
done

{
  echo "## App E2E — fluxos de tela no emulador"
  echo ""
  echo "| Fluxo | Resultado |"
  echo "| --- | --- |"
  for entry in "${RESULTS[@]}"; do
    echo "| \`${entry%%|*}\` | ${entry#*|} |"
  done
  echo ""
  echo "Cada fluxo que falha é repetido uma única vez. Evidências (log e capturas do Maestro, logcat, log da API):"
  echo "artefato \`app-e2e-evidencias\` desta execução."
} | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"

if [ "$failed_required" != "0" ]; then
  echo "APP E2E FAILED"
  exit 1
fi
echo "APP E2E PASSED"
