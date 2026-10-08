#!/usr/bin/env bash
# Runs the Maestro screen tests of the "App E2E" workflow on an Android emulator that is ALREADY running, with
# the test API already answering on this machine (scripts/app-e2e-api.sh start).
#
# - waits until Android is really ready (boot flags, package manager, a focused window, load settling) and
#   closes error dialogs of OTHER packages ("System UI isn't responding") that a slow emulator shows;
# - a crash or an ANR of the APP UNDER TEST — seen as a dialog or in the system log — is never waved away:
#   it is recorded, shown at the top of the summary, and the run ends red even if every flow passed;
# - installs the test APK (scripts/app-e2e-build-apk.sh) and the notification test double
#   (scripts/app-e2e-notification-stub.sh);
# - WARM-UP: opens the app once and waits for the login screen. If it does not appear, the reason is printed
#   in the job log (process, focused window, crash log, filtered logcat, texts on the screen) and the run
#   ends red at once, without trying the flows;
# - runs every flow of mobile/tests/e2e/flows/ in order; a flow that fails is repeated ONCE, and a flow that
#   only passed on the repetition is reported as such in the summary; every failure prints the same diagnosis;
# - stops early when two flows in a row fail because the app did not open, and when the time budget is spent:
#   the script always ends by itself, so the job is never cancelled by its timeout and the evidence is uploaded;
# - drives flow 07 itself, because Maestro cannot run adb: grant the notification access, run the consent part,
#   post the made-up bank notification, run the part that checks the transaction;
# - keeps the evidence (Maestro logs and screenshots, logcat, diagnoses) under the evidence directory;
# - writes a table to the job summary and exits 1 when a required flow failed or did not run.
#
# Usage: scripts/app-e2e-run-flows.sh
#        E2E_APK, E2E_STUB_APK, E2E_EVIDENCE_DIR and E2E_API_PORT override the defaults below.
#        E2E_BUDGET_SECONDS (default 1320) is the time the whole script may take.
#        Needs adb and maestro on PATH and exactly one device (or ANDROID_SERIAL set).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FLOWS="$ROOT/mobile/tests/e2e/flows"
APK="${E2E_APK:-$ROOT/app-e2e-out/couplesync-e2e-test.apk}"
STUB_APK="${E2E_STUB_APK:-$ROOT/app-e2e-out/notification-stub.apk}"
EVIDENCE="${E2E_EVIDENCE_DIR:-$ROOT/app-e2e-out/evidence}"
API_BASE="http://127.0.0.1:${E2E_API_PORT:-5000}"

APP_ID="com.couplesync.app"
APP_ACTIVITY="$APP_ID/.MainActivity"
# The first thing every flow waits for: the link to the sign-up screen, on the login screen
# (accessibilityLabel in mobile/app/(auth)/login.tsx).
LOGIN_LABEL="Ir para o cadastro de conta nova"
# The listener declared by mobile/plugins/withNotificationListener.js (class in mobile/android-native/).
LISTENER="com.couplesync.app/com.couplesync.app.NotificationCaptureService"
# The test double (mobile/tests/e2e/notification-stub/): its package is one the listener accepts.
STUB_ACTIVITY="com.nu.production/com.couplesync.e2estub.PostNotificationActivity"
CAPTURE_FLOW="07-captura-de-notificacao"

# Every NN-*.yaml of the flows directory must be listed here (checked below): a new flow never goes unrun.
FLOW_NAMES=(
  "01-cadastro-e-grupo"
  "02-login-e-logout"
  "03-entrar-em-grupo"
  "04-transacao-manual"
  "05-metas-e-rendas"
  "06-abas"
  "$CAPTURE_FLOW"
  "08-ia-ativar-e-assistente"
)
# Flows listed here are reported but do not fail the job. Empty on purpose: every flow is required.
INFORMATIONAL_FLOWS=()

# Time. The whole script has a budget; what is left of it caps each Maestro run.
BUDGET_SECONDS="${E2E_BUDGET_SECONDS:-1320}"
FLOW_TIMEOUT_SECONDS=600        # one Maestro run (a normal flow takes 1 to 3 minutes)
SYSTEM_READY_TIMEOUT_SECONDS=180
# After boot the emulator is busy with first-boot work. The wait ends when the 1-minute load drops below
# SETTLE_LOAD or, at the latest, after SETTLE_TIMEOUT_SECONDS. On the hosted runner (3 cores) the load was
# still 12 after the whole wait in the run that passed: there this is, in practice, a fixed pause of 90 s —
# the one configuration proven to work, so it is kept as it is. The log says which of the two ended the wait.
SETTLE_TIMEOUT_SECONDS=90
SETTLE_LOAD=6
WARMUP_TIMEOUT_SECONDS=240      # first launch of a release build on a software-rendered emulator is slow
STARTED_AT="$(date +%s)"

export MAESTRO_CLI_NO_ANALYTICS=1
export MAESTRO_CLI_ANALYSIS_NOTIFICATION_DISABLED=true
export MAESTRO_DRIVER_STARTUP_TIMEOUT="${MAESTRO_DRIVER_STARTUP_TIMEOUT:-180000}"

fail() { echo "FAIL: $*" >&2; exit 1; }
elapsed() { echo $(( $(date +%s) - STARTED_AT )); }
remaining() { echo $(( BUDGET_SECONDS - $(elapsed) )); }
# The device shell ends lines with CR on some hosts.
device() { adb shell "$@" 2>/dev/null | tr -d '\r'; }

is_informational() {
  local name
  for name in "${INFORMATIONAL_FLOWS[@]:-}"; do
    [ "$name" = "$1" ] && return 0
  done
  return 1
}

focused_window() { device dumpsys window | grep -E 'mCurrentFocus|mFocusedApp' | sed 's/^ *//' | tr '\n' ' '; }

# Texts and content descriptions on the screen, one per line, as uiautomator sees them. Prints a line starting
# with "(no hierarchy" when the dump fails — which happens while something on the screen never stops animating
# (a loading spinner, for instance).
screen_texts() {
  local out xml
  out="$(device uiautomator dump /sdcard/app-e2e-window.xml)"
  case "$out" in
    *"dumped to"*) ;;
    *) echo "(no hierarchy: uiautomator said: ${out:-nothing})"; return 0 ;;
  esac
  xml="$(adb exec-out cat /sdcard/app-e2e-window.xml 2>/dev/null)"
  [ -n "${1:-}" ] && printf '%s' "$xml" > "$1"
  printf '%s' "$xml" | grep -oE '(text|content-desc)="[^"]+"' | sort -u
}

# ── Error dialogs ("X isn't responding", "X keeps stopping") ───────────────────────────────────────────────
# They cover every app until someone answers them. Two very different cases:
#   - the dialog is about ANOTHER package (System UI, the launcher, Google Play services... on a slow emulator
#     right after boot): it is closed, counted and reported as a warning, and the run goes on;
#   - the dialog is about the APP UNDER TEST, or its owner cannot be told: that is the defect these tests
#     exist to catch. It is recorded as an app problem and the run ends RED, whatever the flows do afterwards.
# The dialogs are windows named "Application Not Responding: <process>" / "Application Error: <process>" in
# `dumpsys window windows`. (The focused window is not a reliable sign: the dialog is often on the screen while
# the window "in focus" is still the app behind it.)
SYSTEM_DIALOGS=0
APP_PROBLEMS=()

# error_dialogs: one line per error dialog on the screen now, "<kind>: <owner process>".
error_dialogs() {
  device dumpsys window windows \
    | grep -oE 'Window\{[^}]*Application (Not Responding|Error):[^}]*\}' \
    | sed -E 's/^.*(Application (Not Responding|Error)): *([^}]*)\}$/\1: \3/' | sort -u
}

# is_app_dialog <line of error_dialogs>: true when it is about the app under test — or about nobody we can name.
is_app_dialog() {
  local owner="${1##*: }"
  [ -z "$owner" ] || [ "$owner" = "$1" ] || [ "$owner" = "$APP_ID" ] || [ "${owner#"$APP_ID":}" != "$owner" ]
}

# record_app_problem <context> <what>: keeps it for the summary and the exit code, with the evidence of now.
record_app_problem() {
  local context="$1" what="$2" n=$(( ${#APP_PROBLEMS[@]} + 1 ))
  APP_PROBLEMS+=("$what ($context)")
  echo "::error::Problem of the app under test — $what ($context). The run will end red."
  adb exec-out screencap -p > "$EVIDENCE/diagnostics/app-problem-$n.png" 2>/dev/null || true
  {
    echo "$(date -u +%H:%M:%S) $context: $what"
    echo "on the screen: $(screen_texts "$EVIDENCE/diagnostics/app-problem-$n.xml" | sed -n '1,12p' | tr '\n' ' ')"
    adb logcat -b crash -d -t 40 2>/dev/null | tr -d '\r'
    adb logcat -d -v time 2>/dev/null | tr -d '\r' | grep -E "ANR in|Reason: |$APP_ID.*(has died|crash)" | tail -n 20
  } > "$EVIDENCE/diagnostics/app-problem-$n.txt" 2>&1
  sed -n '1,25s/^/     /p' "$EVIDENCE/diagnostics/app-problem-$n.txt"
}

# tap_dialog_button <button text...>: taps the first of these buttons found on the screen. 1 when none is there.
tap_dialog_button() {
  local xml="$EVIDENCE/diagnostics/dialog-window.xml" label numbers
  screen_texts "$xml" >/dev/null
  for label in "$@"; do
    numbers="$(grep -oE "text=\"$label\"[^>]*bounds=\"\[[0-9]+,[0-9]+\]\[[0-9]+,[0-9]+\]\"" "$xml" 2>/dev/null \
      | sed -n '1p' | grep -oE '\[[0-9]+,[0-9]+\]\[[0-9]+,[0-9]+\]' | tr -c '0-9\n' ' ')"
    if [ -n "$numbers" ]; then
      # shellcheck disable=SC2086
      set -- $numbers
      echo "tapping \"$label\" at $(( ($1 + $3) / 2 )),$(( ($2 + $4) / 2 ))"
      device input tap "$(( ($1 + $3) / 2 ))" "$(( ($2 + $4) / 2 ))"
      return 0
    fi
  done
  return 1
}

# dismiss_system_dialogs <context>: looks for error dialogs, records each one and closes it (a dialog of the
# app is closed too — after being recorded — so that the rest of the run can still be diagnosed).
dismiss_system_dialogs() {
  local context="$1" dialogs dialog attempt says=""
  for attempt in 1 2 3; do
    dialogs="$(error_dialogs)"
    [ -n "$dialogs" ] || return 0
    while IFS= read -r dialog <&3; do
      [ -n "$dialog" ] || continue
      if is_app_dialog "$dialog"; then
        # Recorded once per dialog, not once per attempt to close it.
        [ "$attempt" = "1" ] && record_app_problem "$context" "diálogo do sistema \"$dialog\""
      else
        if [ "$attempt" = "1" ]; then
          SYSTEM_DIALOGS=$((SYSTEM_DIALOGS + 1))
          adb exec-out screencap -p > "$EVIDENCE/diagnostics/system-dialog-$SYSTEM_DIALOGS.png" 2>/dev/null || true
          # What the dialog says (title, buttons), with its hierarchy kept next to the screenshot.
          says="$(screen_texts "$EVIDENCE/diagnostics/system-dialog-$SYSTEM_DIALOGS.xml" | sed -n '1,12p' | tr '\n' ' ')"
          echo "$(date -u +%H:%M:%S) $context: $dialog | on the screen: $says" >> "$EVIDENCE/diagnostics/system-dialogs.txt"
        fi
        echo "::warning::System dialog of another package on the screen ($context, attempt $attempt): $dialog | on the screen: $says"
      fi
    done 3<<< "$dialogs"   # descriptor 3: adb reads the standard input and would swallow the list
    # The polite way first (error dialogs listen to it); then the buttons: "Wait", and on the last attempt
    # "Close app". A key press is never sent: with the dialog gone it would land on the app.
    device am broadcast -a android.intent.action.CLOSE_SYSTEM_DIALOGS >/dev/null
    sleep 2
    if [ -n "$(error_dialogs)" ]; then
      if [ "$attempt" = "3" ]; then
        tap_dialog_button "Close app" "Fechar app" "Wait" "Aguardar" || true
      else
        tap_dialog_button "Wait" "Aguardar" || true
      fi
      sleep 2
    fi
  done
  dialogs="$(error_dialogs)"
  [ -z "$dialogs" ] || echo "::warning::Error dialog still on the screen after three attempts ($context): $dialogs"
  return 0
}

# check_app_health <context>: a crash or an ANR of the app under test since the last check, as the system
# logged it — whether or not a dialog was (still) on the screen when we looked. Read from the logcat file this
# script records from its start, so nothing is lost when the device buffer rotates. Only lines stamped after
# the start of the script count (LOG_START, device clock): the buffer may still hold older ones.
LOG_START=""
# Lines of the recorded logcat ("MM-DD HH:MM:SS.mmm ...") stamped at or after LOG_START.
lines_since_start() {
  awk -v start="$LOG_START" '{ sub(/\r$/, "") } substr($0, 1, 14) >= start' "$EVIDENCE/logcat.txt" 2>/dev/null
}
SEEN_APP_ANRS=0
SEEN_APP_CRASHES=0
check_app_health() {
  local context="$1" anrs crashes
  anrs="$(lines_since_start | grep -cE "ActivityManager.*ANR in $APP_ID( |$)" || true)"
  crashes="$(lines_since_start | grep -cE "AndroidRuntime.*Process: $APP_ID(,|:| |$)" || true)"
  anrs="${anrs:-0}"; crashes="${crashes:-0}"
  [ "$anrs" -gt "$SEEN_APP_ANRS" ] && record_app_problem "$context" "o app parou de responder (ANR) $(( anrs - SEEN_APP_ANRS )) vez(es)"
  [ "$crashes" -gt "$SEEN_APP_CRASHES" ] && record_app_problem "$context" "o app caiu (exceção fatal) $(( crashes - SEEN_APP_CRASHES )) vez(es)"
  SEEN_APP_ANRS="$anrs"; SEEN_APP_CRASHES="$crashes"
  return 0
}

# Everything needed to tell WHY the app is not where it should be, printed in the job log and kept as evidence.
print_diagnostics() {
  local label="$1" dir="$EVIDENCE/diagnostics" texts
  echo "::group::Diagnosis — $label"
  echo "-- time since the script started: $(elapsed)s; emulator load: $(device cat /proc/loadavg)"
  echo "-- boot: sys.boot_completed=$(device getprop sys.boot_completed) dev.bootcomplete=$(device getprop dev.bootcomplete)"
  echo "-- process of the app (pidof $APP_ID): $(device pidof "$APP_ID" || true)"
  echo "-- focused window: $(focused_window)"
  echo "-- resumed activity: $(device dumpsys activity activities | grep -E 'topResumedActivity|ResumedActivity' | sed 's/^ *//' | tr '\n' ' ')"
  echo "-- memory: $(device cat /proc/meminfo | grep -E 'MemTotal|MemAvailable' | tr -s ' ' | tr '\n' ' ')"
  adb exec-out screencap -p > "$dir/$label.png" 2>/dev/null || true
  echo "-- texts on the screen (screenshot: diagnostics/$label.png):"
  texts="$(screen_texts "$dir/$label.xml")"
  case "$texts" in
    "(no hierarchy"*)
      # The Maestro driver may still hold the accessibility connection: stop it and ask once more.
      device am force-stop dev.mobile.maestro.test; device am force-stop dev.mobile.maestro; sleep 2
      texts="$(screen_texts "$dir/$label.xml")"
      ;;
  esac
  printf '%s\n' "$texts" | sed -n '1,60s/^/     /p'
  echo "-- crash buffer (logcat -b crash, last 60 lines):"
  adb logcat -b crash -d -t 60 2>/dev/null | tr -d '\r' | sed 's/^/     /'
  echo "-- logcat, lines about the app, crashes and ANRs (last 80):"
  adb logcat -d -v time 2>/dev/null | tr -d '\r' \
    | grep -v ' I/Maestro' \
    | grep -E 'E/AndroidRuntime|ReactNative|ReactNativeJS|couplesync|FATAL|ANR in|Application Not Responding|am_anr|am_crash|Force finishing|Process .* has died' \
    | tail -n 80 | sed 's/^/     /'
  echo "::endgroup::"
}

# wait_for_system: Android answers, has a window in focus and the load after boot has dropped.
wait_for_system() {
  local deadline=$(( $(date +%s) + SYSTEM_READY_TIMEOUT_SECONDS )) boot dev focus load
  echo "== waiting for Android to be ready"
  timeout 120 adb wait-for-device || { echo "no device answered adb within 120s"; return 1; }
  while :; do
    boot="$(device getprop sys.boot_completed)"; dev="$(device getprop dev.bootcomplete)"
    focus="$(device dumpsys window | grep -E 'mCurrentFocus' | sed 's/^ *//')"
    if [ "$boot" = "1" ] && [ "$dev" = "1" ] && device pm path android | grep -q '^package:' \
       && [ -n "$focus" ] && [ "${focus#*null}" = "$focus" ]; then
      break
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      echo "Android is not ready after ${SYSTEM_READY_TIMEOUT_SECONDS}s: boot=$boot dev=$dev focus=$focus"
      return 1
    fi
    sleep 3
  done
  echo "ready after $(elapsed)s: $focus"
  deadline=$(( $(date +%s) + SETTLE_TIMEOUT_SECONDS ))
  while :; do
    load="$(device cat /proc/loadavg | cut -d ' ' -f 1)"
    if [ "${load%%.*}" -lt "$SETTLE_LOAD" ] 2>/dev/null || [ "$(date +%s)" -ge "$deadline" ]; then
      break
    fi
    sleep 5
  done
  if [ "${load%%.*}" -lt "$SETTLE_LOAD" ] 2>/dev/null; then
    echo "emulator settled: load (1 min) $load, $(elapsed)s since the start"
  else
    echo "emulator did NOT settle (load $load, limit $SETTLE_LOAD): the ${SETTLE_TIMEOUT_SECONDS}s pause ran in full; going on"
  fi
  dismiss_system_dialogs "after boot"
}

# warm_up: the app opens and shows the login screen. Returns 1 (after printing the diagnosis) when it does not.
WARMUP_RESULT="não rodou"
warm_up() {
  local began deadline pid texts last_report=0 now seen=1 died=0
  echo "== warm-up: first launch of the app (up to ${WARMUP_TIMEOUT_SECONDS}s)"
  device pm clear "$APP_ID" >/dev/null
  adb shell am start -W -n "$APP_ACTIVITY" 2>&1 | tr -d '\r'
  began="$(date +%s)"; deadline=$(( began + WARMUP_TIMEOUT_SECONDS ))
  while :; do
    dismiss_system_dialogs "warm-up"
    pid="$(device pidof "$APP_ID" || true)"
    texts="$(screen_texts)"
    if printf '%s\n' "$texts" | grep -qF "content-desc=\"$LOGIN_LABEL\""; then
      seen=0
      break
    fi
    now="$(date +%s)"
    if [ -z "$pid" ]; then
      died=$((died + 1))
      echo "$(( now - began ))s: the app has no process (count $died)"
      # Gone three checks in a row: it crashed or was killed; waiting longer will not bring it back.
      [ "$died" -ge 3 ] && break
    else
      died=0
    fi
    if [ $(( now - last_report )) -ge 20 ]; then
      last_report="$now"
      echo "$(( now - began ))s: pid=${pid:-none}; $(focused_window); screen: $(printf '%s\n' "$texts" | sed -n '1,6p' | tr '\n' ' ')"
    fi
    [ "$now" -ge "$deadline" ] && break
    sleep 4
  done
  if [ "$seen" = "0" ]; then
    WARMUP_RESULT="tela de login em $(( $(date +%s) - began ))s"
    echo "== warm-up: login screen after $(( $(date +%s) - began ))s"
    return 0
  fi
  echo "== warm-up: adb did not see the login screen; asking Maestro (second opinion, same check the flows do)"
  if maestro_run "00-aquecimento" "$FLOWS/partes/00-aquecimento.yaml"; then
    WARMUP_RESULT="tela de login vista só pelo Maestro (adb não viu) em $(( $(date +%s) - began ))s"
    echo "::warning::Warm-up: Maestro sees the login screen but uiautomator over adb does not."
    return 0
  fi
  WARMUP_RESULT="FALHOU: a tela de login não apareceu"
  echo "::error::Warm-up failed: the app did not show the login screen. See the diagnosis below."
  print_diagnostics "aquecimento"
  return 1
}

# maestro_run <label> <flow file>: one Maestro run; its log, report and screenshots go to the evidence
# directory. Returns 124 when there is no time left for it.
maestro_run() {
  local label="$1" file="$2" status limit
  limit=$(( $(remaining) - 20 ))
  [ "$limit" -gt "$FLOW_TIMEOUT_SECONDS" ] && limit="$FLOW_TIMEOUT_SECONDS"
  if [ "$limit" -lt 60 ]; then
    echo "--- maestro: $label NOT RUN (time budget of ${BUDGET_SECONDS}s spent)"
    return 124
  fi
  echo "--- maestro: $label (limit ${limit}s, $(elapsed)s since the start)"
  # In the background and waited for: bash only runs a signal trap between foreground commands, and the
  # summary must be written at once when the hard ceiling of the workflow step ends this script (TERM).
  (
    timeout "$limit" maestro test \
      --format junit --output "$EVIDENCE/maestro/$label.xml" \
      --debug-output "$EVIDENCE/maestro/$label" --flatten-debug-output \
      "$file" 2>&1 | tee "$EVIDENCE/maestro/$label.log"
    exit "${PIPESTATUS[0]}"
  ) &
  wait "$!"
  status=$?
  if [ "$status" != "0" ]; then
    echo "--- maestro: $label failed (exit $status)"
    grep -E 'Failed|Assertion|not found|Exception' "$EVIDENCE/maestro/$label.log" | sed -n '1,10s/^/     /p'
    [ "$label" = "00-aquecimento" ] || print_diagnostics "$label"
  fi
  return "$status"
}

# The capture flow: steps 1 to 4 described in 07-captura-de-notificacao.yaml.
run_capture_flow() {
  local label="$1" status=0
  adb shell pm clear "$APP_ID" >/dev/null || return 1
  adb shell cmd notification allow_listener "$LISTENER" || return 1
  # The exit code of Maestro is kept: 124 (no time left) must reach the caller as it is.
  maestro_run "$label-consentimento" "$FLOWS/$CAPTURE_FLOW.yaml"; status=$?
  if [ "$status" = "0" ]; then
    # The made-up notification, posted by the test double as a bank app would.
    if adb shell am start -W -n "$STUB_ACTIVITY"; then
      maestro_run "$label-transacao" "$FLOWS/partes/07-conferir-transacao-capturada.yaml"; status=$?
    else
      status=1
    fi
  fi
  # The access is taken back so that it never leaks into another flow (they must not see the consent screen).
  adb shell cmd notification disallow_listener "$LISTENER" || true
  return "$status"
}

run_flow() {
  local name="$1" attempt="$2"
  dismiss_system_dialogs "before $name"
  if [ "$name" = "$CAPTURE_FLOW" ]; then
    run_capture_flow "$name-tentativa$attempt"
  else
    maestro_run "$name-tentativa$attempt" "$FLOWS/$name.yaml"
  fi
}

# did_not_open <flow name>: its last attempt stopped at the very first wait, the login screen.
did_not_open() {
  grep -qF "$LOGIN_LABEL" "$EVIDENCE"/maestro/"$1"-tentativa2*.log 2>/dev/null \
    && grep -q 'Failed' "$EVIDENCE"/maestro/"$1"-tentativa2*.log 2>/dev/null
}


# write_summary: the table of the job summary. Written once — at the normal end, or by the exit trap when the
# script is stopped before it (the hard `timeout` of the workflow step, a failed precondition).
SUMMARY_WRITTEN=0
INTERRUPTED=""
write_summary() {
  [ "$SUMMARY_WRITTEN" = "0" ] || return 0
  SUMMARY_WRITTEN=1
  {
    local entry name problem listed_results
    echo "## App E2E — fluxos de tela no emulador"
    echo ""
    if [ "${#APP_PROBLEMS[@]}" -gt 0 ]; then
      echo "> [!CAUTION]"
      echo "> **O app travou ou caiu durante os testes. O job falha por isso, mesmo que os fluxos tenham passado:**"
      for problem in "${APP_PROBLEMS[@]}"; do
        echo "> - $problem"
      done
      echo ""
    fi
    [ -z "$INTERRUPTED" ] || { echo "**Execução interrompida antes do fim: $INTERRUPTED.**"; echo ""; }
    echo "- Aquecimento (primeira abertura do app): $WARMUP_RESULT"
    echo "- Travamentos ou quedas do próprio app: ${#APP_PROBLEMS[@]}"
    echo "- Diálogos \"não está respondendo\" de OUTROS pacotes (sistema), fechados: $SYSTEM_DIALOGS"
    echo "- Tempo do script: $(elapsed)s de ${BUDGET_SECONDS}s"
    echo ""
    echo "| Fluxo | Resultado |"
    echo "| --- | --- |"
    listed_results=" "
    for entry in "${RESULTS[@]:-}"; do
      [ -n "$entry" ] || continue
      echo "| \`${entry%%|*}\` | ${entry#*|} |"
      listed_results="$listed_results${entry%%|*} "
    done
    for name in "${FLOW_NAMES[@]}"; do
      case "$listed_results" in
        *" $name "*) ;;
        *) echo "| \`$name\` | não rodou (execução interrompida) |" ;;
      esac
    done
    echo ""
    echo "Cada fluxo que falha é repetido uma única vez. Evidências (log e capturas do Maestro, logcat, diagnósticos,"
    echo "log da API): artefato \`app-e2e-evidencias\` desta execução; o diagnóstico de cada falha está no log do job."
  } | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
}

command -v adb >/dev/null || fail "adb is not on PATH"
command -v maestro >/dev/null || fail "maestro is not on PATH"
[ -f "$APK" ] || fail "test APK not found: $APK"
[ -f "$STUB_APK" ] || fail "notification test double not found: $STUB_APK"
[ "$(curl --silent --output /dev/null --max-time 10 --write-out '%{http_code}' "$API_BASE/health/ready" || true)" = "200" ] \
  || fail "the test API is not answering at $API_BASE/health/ready"

# An empty list and an empty directory would "pass" with nothing run.
[ "${#FLOW_NAMES[@]}" -gt 0 ] || fail "FLOW_NAMES is empty: there is nothing to run"
listed="$(printf '%s\n' "${FLOW_NAMES[@]}" | sort)"
on_disk="$(find "$FLOWS" -maxdepth 1 -type f -name '[0-9][0-9]-*.yaml' -exec basename {} .yaml \; | sort)"
[ "$listed" = "$on_disk" ] || fail "FLOW_NAMES and the NN-*.yaml files of $FLOWS differ. Listed: $(echo $listed) | On disk: $(echo $on_disk)"

mkdir -p "$EVIDENCE/maestro" "$EVIDENCE/diagnostics"
maestro --version || fail "maestro does not start"

RESULTS=()
adb logcat -c >/dev/null 2>&1 || true
LOG_START="$(device "date '+%m-%d %H:%M:%S'")"
[ "${#LOG_START}" = "14" ] || fail "could not read the clock of the device (got: $LOG_START)"
adb logcat -v time > "$EVIDENCE/logcat.txt" 2>&1 &
LOGCAT_PID=$!
finish() {
  local status=$?
  if [ "$SUMMARY_WRITTEN" = "0" ]; then
    [ -n "$INTERRUPTED" ] || INTERRUPTED="o script terminou com o código $status antes de escrever o resumo"
    write_summary
    echo "APP E2E FAILED"
  fi
  adb logcat -b crash -d > "$EVIDENCE/logcat-crash.txt" 2>/dev/null || true
  kill "$LOGCAT_PID" >/dev/null 2>&1 || true
}
trap finish EXIT
# The hard ceiling of the workflow step (`timeout`) ends the script with TERM: the summary is still written.
trap 'INTERRUPTED="o teto de tempo do passo foi atingido (sinal de término)"; exit 143' TERM INT

if ! wait_for_system; then
  print_diagnostics "sistema"
  WARMUP_RESULT="não rodou: o Android não ficou pronto"
  for name in "${FLOW_NAMES[@]}"; do RESULTS+=("$name|não rodou (o Android não ficou pronto)"); done
  write_summary
  echo "APP E2E FAILED"
  exit 1
fi

echo "== installing the test APK and the notification test double"
adb install -r "$APK" || fail "could not install the test APK"
# -g: the test double may post notifications without anyone answering the permission dialog.
adb install -r -g "$STUB_APK" || fail "could not install the notification test double"
adb shell cmd notification disallow_listener "$LISTENER" >/dev/null 2>&1 || true

if ! warm_up; then
  sleep 1
  check_app_health "aquecimento"
  for name in "${FLOW_NAMES[@]}"; do RESULTS+=("$name|não rodou (o app não abriu no aquecimento)"); done
  write_summary
  echo "APP E2E FAILED"
  exit 1
fi
sleep 1
check_app_health "aquecimento"

failed_required=0
not_open_in_a_row=0
stop_reason=""
for name in "${FLOW_NAMES[@]}"; do
  if [ -n "$stop_reason" ]; then
    RESULTS+=("$name|não rodou ($stop_reason)")
    is_informational "$name" || failed_required=1
    continue
  fi
  echo "== flow $name"
  problems_before="${#APP_PROBLEMS[@]}"
  run_flow "$name" 1; status=$?
  if [ "$status" = "0" ]; then
    result="passou"
  elif [ "$status" = "124" ] && [ "$(remaining)" -lt 80 ]; then
    result="não rodou (tempo esgotado)"
    stop_reason="tempo esgotado"
  else
    echo "== flow $name failed: repeating once"
    run_flow "$name" 2; status=$?
    if [ "$status" = "0" ]; then
      result="passou só na repetição"
    elif [ "$status" = "124" ] && [ "$(remaining)" -lt 80 ]; then
      result="FALHOU na 1ª tentativa; a repetição não rodou (tempo esgotado)"
      stop_reason="tempo esgotado"
    else
      result="FALHOU"
      is_informational "$name" && result="FALHOU (informativo: não derruba o job)"
    fi
  fi
  case "$result" in
    passou*) not_open_in_a_row=0 ;;
    *)
      is_informational "$name" || failed_required=1
      if did_not_open "$name"; then
        not_open_in_a_row=$((not_open_in_a_row + 1))
        result="$result — o app não chegou à tela de login"
      else
        not_open_in_a_row=0
      fi
      if [ -z "$stop_reason" ] && [ "$not_open_in_a_row" -ge 2 ]; then
        stop_reason="dois fluxos seguidos sem o app abrir"
        echo "::error::Two flows in a row failed because the app did not open: stopping."
      fi
      ;;
  esac
  # A crash or an ANR of the app during this flow fails the run even when the flow itself passed.
  sleep 1
  dismiss_system_dialogs "after $name"
  check_app_health "fluxo $name"
  [ "${#APP_PROBLEMS[@]}" -gt "$problems_before" ] && result="$result — ATENÇÃO: o app travou ou caiu durante este fluxo"
  if [ -z "$stop_reason" ] && [ "$(remaining)" -lt 80 ]; then
    stop_reason="tempo esgotado"
    echo "::error::Time budget of ${BUDGET_SECONDS}s spent: stopping."
  fi
  echo "== flow $name: $result"
  RESULTS+=("$name|$result")
done

write_summary

if [ "${#APP_PROBLEMS[@]}" -gt 0 ]; then
  echo "::error::The app under test stopped responding or crashed during the run (${#APP_PROBLEMS[@]} record(s)): the job fails."
  echo "APP E2E FAILED"
  exit 1
fi
if [ "$failed_required" != "0" ]; then
  echo "APP E2E FAILED"
  exit 1
fi
echo "APP E2E PASSED"
