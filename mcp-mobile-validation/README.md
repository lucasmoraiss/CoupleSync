# MCP Mobile Validation

MCP (Model Context Protocol) server that gives autonomous AI agents the ability to **see** and **interact** with the CoupleSync Android app running on an emulator or physical device.

This is the mobile equivalent of Chrome DevTools MCP for web apps — it provides a feedback loop so agents can validate their implementations visually before completing tasks.

## Architecture

Two layers of tools:

| Layer | Technology | Use Case |
|-------|-----------|----------|
| **Low-level** | ADB (Android Debug Bridge) | Screenshots, UI hierarchy dumps, tap/swipe, text input, logcat |
| **High-level** | Maestro | Predefined flow execution, reliable element matching, multi-step validation |

## Prerequisites

### 1. Android SDK Platform Tools (ADB)

ADB must be available. Typically installed with Android Studio.

```powershell
# Verify ADB is in PATH
adb version

# If not, add to PATH (typical Windows location):
# C:\Users\<user>\AppData\Local\Android\Sdk\platform-tools\
```

### 2. Android Emulator Running

```powershell
# List available emulators
emulator -list-avds

# Start an emulator
emulator -avd <avd_name>

# OR verify connected device
adb devices
```

### 3. CoupleSync Dev Build Installed

```powershell
# From mobile/ directory, build the dev client
cd mobile
npx expo prebuild
npx expo run:android

# OR install a pre-built APK
adb install path/to/couplesync-dev.apk
```

### 4. Maestro (Optional, for flow execution)

```powershell
# Install Maestro (Windows — requires WSL or use PowerShell installer)
# See: https://maestro.mobile.dev/getting-started/installing-maestro

# On Windows with Chocolatey:
choco install maestro

# Verify
maestro --version
```

## Setup

```powershell
cd mcp-mobile-validation
npm install
npm run build
```

The MCP server is configured in `.vscode/mcp.json` and will be available to Copilot agents automatically.

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ADB_PATH` | `adb` | Path to ADB executable |
| `MAESTRO_PATH` | `maestro` | Path to Maestro executable |
| `SCREENSHOT_DIR` | `os.tmpdir()/mcp-mobile-screenshots` | Where screenshots are saved |
| `APP_PACKAGE` | `com.couplesync.app` | Android package name |
| `MAESTRO_FLOWS_DIR` | _(empty)_ | Directory containing Maestro flow YAML files |

## Available Tools

### Inspection (Safe, read-only)

| Tool | Description |
|------|-------------|
| `list_devices` | List connected devices/emulators |
| `take_screenshot` | Capture screen as PNG (returns base64 image) |
| `get_ui_hierarchy` | Dump UI tree as XML (text, IDs, bounds, accessibility) |
| `find_element` | Search UI tree by text/id/description |
| `validate_screen` | Screenshot + hierarchy in one call (**preferred**) |
| `assert_visible` | Pass/fail assertion on element visibility |
| `wait_for_element` | Poll until element appears (with timeout) |
| `get_app_state` | Check if app is running/foreground |
| `get_screen_info` | Screen dimensions and density |
| `get_logcat` | Read device logs (filter by tag/level) |

### Interaction (Mutates device state)

| Tool | Description |
|------|-------------|
| `launch_app` | Start/restart app (optional: clear data, fresh start) |
| `tap_element` | Tap by coordinates or by text/description lookup |
| `input_text` | Type text into focused field |
| `swipe` | Swipe gesture (scroll, pull-to-refresh) |
| `press_key` | Press hardware key (BACK, HOME, ENTER) |

### Maestro (Flow execution)

| Tool | Description |
|------|-------------|
| `run_maestro_flow` | Execute a YAML flow file |
| `run_maestro_command` | Execute inline Maestro commands |
| `list_maestro_flows` | List available flow files |

## Agent Workflow Integration

### Coder Agent (after implementing UI)
```
launch_app → navigate to screen → validate_screen → assert key elements → check logcat for errors
```

### QA Agent (validation pass)
```
run_maestro_flow("smoke-launch.yaml") → run_maestro_flow("login.yaml") → validate critical paths
```

### Reviewer Agent (sanity check)
```
validate_screen on key screens → verify no JS errors → check accessibility labels
```

## Writing Maestro Flows

Place flows in `mobile/tests/e2e/flows/`. Existing flows:

- `smoke-launch.yaml` — App launches to login screen
- `login.yaml` — Login with test credentials (pass EMAIL/PASSWORD env vars)
- `dashboard-check.yaml` — Dashboard has expected content
- `navigation-tabs.yaml` — All main tabs are navigable

### Maestro YAML Basics

```yaml
appId: com.couplesync.app
---
- launchApp
- assertVisible: "Expected Text"
- tapOn: "Button Text"
- inputText: "typed value"
- assertVisible:
    text: ".*regex.*"
    isRegex: true
    timeout: 5000
```

See [Maestro docs](https://maestro.mobile.dev/) for full reference.

## Troubleshooting

| Issue | Solution |
|-------|----------|
| `adb: command not found` | Add Android SDK platform-tools to PATH |
| `no devices/emulators found` | Start emulator or connect USB device with debugging enabled |
| `uiautomator dump` returns empty | Wait for app to fully render (use `wait_for_element`) |
| Maestro timeout | Increase timeout in flow, or check if app is stuck on loading |
| Screenshots are black | Emulator may have secure flag; try physical device |
