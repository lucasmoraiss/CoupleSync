# CoupleSync Mobile E2E Manual Walkthrough

> **Scope:** AC-001 through AC-010  
> **Traces:** T-029  
> **Prerequisites:** Backend running, pilot seed executed (`scripts/pilot-seed/seed.ps1`), Android device/emulator with CoupleSync APK installed.

---

## Automated screen tests — what is already covered and what stays manual

On every pull request to `main`, the **App E2E** workflow (`.github/workflows/app-e2e.yml`) builds a test APK
from the pull request, installs it on an Android emulator (API 34) and runs the Maestro flows of
`mobile/tests/e2e/flows/` against the real API container image with a throwaway PostgreSQL. Each flow creates
its own account (a unique e-mail per run) and does not depend on the others.

| Flow | What it checks on the screen |
| --- | --- |
| `01-cadastro-e-grupo.yaml` | Sign up, create a group, see the 8-character invite code, reach the Painel |
| `02-login-e-logout.yaml` | Sign out (confirming the dialog), sign in again, close and reopen the app and still be signed in |
| `03-entrar-em-grupo.yaml` | A second account joins the first one's group with the invite code read from the screen; both members are listed |
| `04-transacao-manual.yaml` | Two manual transactions in a row (the form opens empty the second time), edit one, delete the other (confirming the dialog), check the list |
| `05-metas-e-rendas.yaml` | Create a goal and an income source and see them on their screens |
| `06-abas.yaml` | Open every tab; none shows an error state |
| `07-captura-de-notificacao.yaml` + `partes/07-conferir-transacao-capturada.yaml` | With Android's notification access granted, accept the capture consent, receive one made-up bank notification and see it as a transaction |
| `08-ia-ativar-e-assistente.yaml` | On the first use of a group, switch the AI analysis on at the welcome screen, see the Painel without the "desligada" card, open the Assistente from the Painel and get the answer of the fake AI provider of the test API |

Flow 07 is driven by `scripts/app-e2e-run-flows.sh` (Maestro cannot run `adb`). The notification comes from a
test double installed only on the emulator (`mobile/tests/e2e/notification-stub/`), whose application id is one
the notification reader accepts; its text is invented.

**Still manual** (the scenarios below, on a real device, at the owner's checkpoints):

- pairing across two real devices and what the *other* partner sees (Scenarios 1, 4 and 8 — the flows use one emulator);
- the Android "Notification access" settings screen itself and notifications from the real bank apps, including
  every bank format and deduplication of a repeated notification (Scenarios 2, 3 and 9);
- push alerts (Scenario 7 — the test APK has no Firebase configuration);
- statement import (PDF/photo through the system file picker), password recovery and e-mail confirmation
  (they need a real inbox), the arrival of an OTA update and installing a new APK over an old one;
- how things look: layout on other screen sizes, tap-target sizes and load-time measurements (Scenario 10).

To run the flows on your own emulator: build the test APK and the test double with
`scripts/app-e2e-build-apk.sh` and `scripts/app-e2e-notification-stub.sh` (in a throwaway copy of the
repository), start the API with `scripts/app-e2e-api.sh start`, install Maestro (the version pinned in the
workflow) and run `scripts/app-e2e-run-flows.sh`. Never point a test build at the production API.

---

## Scenario 1 — Couple Registration and Pairing (AC-001)

**Preconditions:** Two Android devices (A and B). App installed on both. No existing accounts.

1. [ ] On Device A: open CoupleSync → tap **Register**
2. [ ] Enter name, email, and password → tap **Create Account**
3. [ ] Verify: redirected to onboarding / couple setup screen
4. [ ] On Device A: tap **Create Couple** → note the 6-character join code displayed
5. [ ] On Device B: open CoupleSync → tap **Register** → create a second account
6. [ ] On Device B: tap **Join Couple** → enter the join code from step 4 → tap **Join**
7. [ ] Verify on Device B: dashboard shows shared workspace with both users listed
8. [ ] Verify on Device A: dashboard also shows both partners in the couple

**Expected result:** Both partners see a shared dashboard with consistent data. Join code is single-use.

---

## Scenario 2 — Bank Notification Integration Setup (AC-002)

**Preconditions:** Couple paired (Scenario 1 completed). Nubank or supported bank app installed.

1. [ ] On Device A: tap **Settings** → tap **Bank Integrations**
2. [ ] Tap **Enable Notification Access** → app redirects to Android Notification Access settings
3. [ ] Grant notification access to CoupleSync in Android settings → return to app
4. [ ] Verify: integration status shows **Active** / green indicator
5. [ ] Verify: no banking credentials were requested at any point
6. [ ] Tap **Supported Banks** → verify list includes Nubank, Itaú, Inter, C6, Bradesco

**Expected result:** Integration enabled without any banking credentials. Status is Active.

---

## Scenario 3 — Transaction Capture and Deduplication (AC-003)

**Preconditions:** Notification listener enabled (Scenario 2). Nubank app installed on Device A.

1. [ ] On Device A: trigger a Nubank push notification (e.g., make a test purchase or use the bank's notification test feature)
2. [ ] Wait up to 60 seconds
3. [ ] Navigate to **Transactions** tab in CoupleSync
4. [ ] Verify: the new transaction appears with correct amount, merchant, and category
5. [ ] On Device B: open **Transactions** tab → verify same transaction is visible
6. [ ] Trigger the **same notification again** (resend scenario)
7. [ ] Wait 30 seconds → verify: no duplicate transaction is created (count stays the same)

**Expected result:** Transaction captured within 60 seconds. Deduplication prevents duplicate records.

---

## Scenario 4 — Dashboard Shared View (AC-004)

**Preconditions:** At least 3 transactions ingested (use pilot seed or manual triggers).

1. [ ] On Device A: tap the **Dashboard** tab (home)
2. [ ] Verify: total expenses display is visible and non-zero
3. [ ] Verify: per-partner breakdown shows amounts for both User A and User B
4. [ ] Verify: expenses by category chart or list is visible
5. [ ] On Device B: tap the **Dashboard** tab
6. [ ] Verify: total expenses and transaction count matches Device A exactly
7. [ ] Verify: partner breakdown identifies User A vs. User B correctly

**Expected result:** Both partners see consistent aggregated financial data.

---

## Scenario 5 — Goals Create, Edit, Archive (AC-005)

**Preconditions:** Couple paired.

1. [ ] Tap **Goals** tab → tap **+ New Goal**
2. [ ] Enter: Name `Viagem Europa`, Target `R$ 5.000,00`, Deadline `01/06/2027` → tap **Save**
3. [ ] Verify: goal appears in list with status **Active** and progress bar at 0%
4. [ ] Tap the goal → tap **Edit** → change target to `R$ 6.000,00` → tap **Save**
5. [ ] Verify: goal shows updated target amount
6. [ ] Tap the goal → tap **Archive** → confirm prompt
7. [ ] Verify: goal moves to archived/hidden state and no longer appears in the active list
8. [ ] Toggle **Show Archived** → verify archived goal reappears

**Expected result:** Full CRUD lifecycle works. Goal transitions Active → Archived cleanly.

---

## Scenario 6 — Projected Cash Flow (AC-006)

**Preconditions:** At least 5 transactions ingested (use pilot seed data).

1. [ ] Tap **Cash Flow** tab
2. [ ] Verify: 30-day projection is displayed (projected balance, income/expense estimate)
3. [ ] Tap **90 days** toggle or selector
4. [ ] Verify: 90-day projection updates to show longer horizon
5. [ ] Verify: projection includes a confidence/assumptions note
6. [ ] Scroll to view recurring or estimated items listed

**Expected result:** Both 30-day and 90-day projections are available and visually distinct.

---

## Scenario 7 — Push Alert Delivery (AC-007)

**Preconditions:** Device token registered. FCM connected. Couple has transactions.

1. [ ] Ensure User A has a balance below the low-balance threshold (adjust seed data if needed)
2. [ ] Via pilot seed or backend, ingest a transaction that drops balance below threshold
3. [ ] Verify: FCM push notification arrives on Device A within 60 seconds with a low-balance alert message
4. [ ] Ingest a large transaction (amount > large-transaction threshold, e.g., R$ 1.000,00)
5. [ ] Verify: FCM push notification for **large transaction** arrives on Device A
6. [ ] Open the notification → verify it deep-links into the transaction details screen

**Expected result:** Three alert types (low balance, large transaction, upcoming bill) trigger FCM delivery.

---

## Scenario 8 — Couple-Level Data Isolation (AC-008)

**Preconditions:** Two separate couples registered (run seed.ps1 twice for different users).

1. [ ] Log in as User A (Couple 1) → open **Transactions** → note transaction IDs
2. [ ] Log in as User C (Couple 2) on the same device (logout first)
3. [ ] Verify: Couple 2 transactions do NOT show any of Couple 1's transactions
4. [ ] Open **Dashboard** as User C → verify totals reflect only Couple 2 data
5. [ ] Attempt to directly access a Couple 1 transaction URL/ID while authenticated as User C
6. [ ] Verify: API returns 403 Forbidden or 404 Not Found — no data leakage

**Expected result:** Complete data isolation between couples. Cross-couple access is denied.

---

## Scenario 9 — Integration Failure Recovery (AC-009)

**Preconditions:** Notification listener enabled.

1. [ ] On Device A: go to Android Settings → Notification Access → revoke CoupleSync permission
2. [ ] Return to CoupleSync → tap **Settings** → **Bank Integrations**
3. [ ] Verify: integration status shows **Disabled** / error indicator with a recovery hint message
4. [ ] Verify: a retry/re-enable button or link to Android settings is visible
5. [ ] Re-grant notification access → return to CoupleSync
6. [ ] Verify: integration status returns to **Active**
7. [ ] Simulate a malformed notification (if test mode available) → verify parser error is logged gracefully with no crash

**Expected result:** Failures surface clear status and recovery guidance. App does not crash.

---

## Scenario 10 — Core UX Tap Targets and Dashboard Load (AC-010)

**Preconditions:** Couple paired, at least 3 transactions ingested.

### 10a — Tap Count Walkthrough

1. [ ] From the Dashboard (tap 0), tap **+ Add Goal** or navigate to Goals tab (tap 1) → tap **+ New Goal** (tap 2) → fill form and tap **Save** (tap 3)
   - **Expected:** Goal created in ≤ 3 taps from dashboard
2. [ ] From the Dashboard (tap 0), tap **Transactions** tab (tap 1) → tap any transaction (tap 2) → tap **Edit Category** (tap 3)
   - **Expected:** Category change reachable in ≤ 3 taps
3. [ ] From the Dashboard (tap 0), tap **Cash Flow** tab (tap 1) → verify projection is immediately visible (tap 2 to expand if collapsed)
   - **Expected:** 30-day projection visible in ≤ 2 taps

### 10b — Dashboard Load Performance

1. [ ] Force-close CoupleSync → reopen
2. [ ] Start a stopwatch when the app becomes interactive
3. [ ] Tap **Dashboard** tab if not default
4. [ ] Measure time until dashboard data is fully loaded (no skeleton/spinner)
5. [ ] Verify: total load time is **under 2.5 seconds** on the test device over stable Wi-Fi
6. [ ] Repeat 3 times and note median time

**Expected result:** Core actions reachable in ≤ 3 taps. Dashboard loads under 2.5 seconds.

---

## Sign-off Checklist

| AC    | Scenario                          | Tester | Date | Pass/Fail | Notes |
|-------|-----------------------------------|--------|------|-----------|-------|
| AC-001 | Couple registration and pairing  |        |      |           |       |
| AC-002 | Bank integration setup            |        |      |           |       |
| AC-003 | Transaction capture/deduplication |        |      |           |       |
| AC-004 | Dashboard shared view             |        |      |           |       |
| AC-005 | Goals CRUD and archive            |        |      |           |       |
| AC-006 | Projected cash flow               |        |      |           |       |
| AC-007 | Push alert delivery               |        |      |           |       |
| AC-008 | Couple-level data isolation       |        |      |           |       |
| AC-009 | Integration failure recovery      |        |      |           |       |
| AC-010 | UX tap targets + dashboard load   |        |      |           |       |
