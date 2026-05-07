# Mobile Validation — Agent Instructions

Use when: an agent needs to validate that a mobile UI implementation is correct before completing a task, finalizing an issue, or making a commit. This applies to any task that modifies screens, navigation, interactions, or visual layout in the React Native / Expo mobile app.

## applyTo
- `mobile/app/**`
- `mobile/src/**`

## When to Validate

Agents MUST perform mobile validation when:
1. A new screen or view was created
2. Navigation flow was changed
3. UI components were added or significantly modified
4. Layout/styling changes that affect user-facing behavior
5. Form interactions, buttons, or touch handlers were modified
6. Accessibility labels or roles were added/changed

Agents MAY skip validation when:
- Changes are purely to types, interfaces, or non-rendered code
- Only constants/config/theme tokens were modified (no layout impact)
- Changes are restricted to API service files with no UI rendering

## Prerequisites

**Agents MUST call `ensure_ready` as the FIRST tool before any other validation tool.** This tool self-heals prerequisites automatically:

1. **ADB**: Detects ADB in PATH or finds it in Android SDK. If not found anywhere, returns fix instructions.
2. **Device/Emulator**: Checks if a device is connected. If not, automatically starts an Android emulator (AVD).
3. **App Installed**: Checks if CoupleSync is on the device. If not, runs `expo prebuild` + `expo run:android` to build and install.
4. **Maestro**: Checks if Maestro is available for flow-based tests (optional — ADB validation works without it).

The agent workflow is:
```
ensure_ready → (prerequisites auto-resolved) → proceed with validation
```

If `ensure_ready` returns `ready: false`, the agent should:
- Report the blocker in task output
- NOT mark the task as blocked — note it as `known_issue` instead
- Continue with non-UI validation (type checking, linting) if possible

If `ensure_ready` returns `ready: true` or `ready_for_adb: true`, proceed with validation normally.

## Validation Strategy

### Quick Validation (per-task, recommended)
Use for most UI tasks. Takes ~30 seconds (assuming prerequisites already resolved).

1. `ensure_ready` — self-heal prerequisites (call once per session, skip if already confirmed)
2. `launch_app` — ensure fresh app state
3. Navigate to the changed screen (via `tap_element` or direct deep link)
4. `validate_screen` — capture screenshot + UI hierarchy
5. `assert_visible` — verify expected elements are present
6. `get_logcat` with tag `ReactNativeJS` — check for JS errors/warnings

### Deep Validation (before milestone/release)
Use for complex flows or integration points.

1. Run relevant Maestro flow: `run_maestro_flow`
2. Validate multiple screens in sequence
3. Check error states (network off, invalid input)
4. Verify accessibility labels with `find_element` + `content_desc`

### Regression Check
After fixing a bug, validate both:
1. The fix works (positive case)
2. Related screens still work (no side effects)

## Available Tools

### Bootstrap (call first, once per session)
| Tool | Purpose |
|------|---------|
| `ensure_ready` | Self-healing prerequisite check: finds ADB, connects device/starts emulator, installs app, checks Maestro |

### Inspection (read-only, safe to call anytime)
| Tool | Purpose |
|------|---------|
| `list_devices` | Verify emulator/device is connected |
| `take_screenshot` | Visual capture of current screen |
| `get_ui_hierarchy` | Full accessibility tree as XML |
| `find_element` | Search for specific element by text/id/desc |
| `validate_screen` | Combined screenshot + hierarchy (preferred) |
| `get_app_state` | Check if app is running/foreground |
| `get_screen_info` | Device dimensions and density |
| `get_logcat` | Read app logs for errors |
| `assert_visible` | Pass/fail assertion on element visibility |
| `wait_for_element` | Poll for element after async action |

### Interaction (mutates device state)
| Tool | Purpose |
|------|---------|
| `launch_app` | Start/restart the app |
| `tap_element` | Tap by coordinates or text/description |
| `input_text` | Type into focused field |
| `swipe` | Scroll or gesture |
| `press_key` | Hardware key (back, home, enter) |

### Flow Execution (Maestro)
| Tool | Purpose |
|------|---------|
| `run_maestro_flow` | Execute a predefined YAML flow |
| `run_maestro_command` | Execute inline Maestro commands |
| `list_maestro_flows` | Discover available flow files |

## Output Format

When reporting validation results, agents should include:

```json
{
  "validation": {
    "status": "pass|fail|skipped",
    "reason": "Why this status (if fail or skipped)",
    "screens_checked": ["screen_name_1", "screen_name_2"],
    "assertions": [
      { "check": "Dashboard title visible", "result": "pass" },
      { "check": "Transaction list populated", "result": "pass" }
    ],
    "screenshots": ["path/to/screenshot1.png"],
    "js_errors": [],
    "notes": "Any observations about the UI state"
  }
}
```

## Integration with Agent Workflow

### Coder Agent
After implementing a UI task:
1. Run Quick Validation on the modified screen(s)
2. Include validation results in task output
3. If validation fails, fix the issue before marking task as `implemented`

### QA Agent
When testing mobile changes:
1. Use Deep Validation strategy
2. Run any relevant Maestro flows
3. Validate edge cases (empty states, error states, loading states)
4. Check accessibility labels

### Reviewer Agent
When reviewing mobile PRs:
1. Use `validate_screen` on key screens for visual sanity check
2. Verify no JS errors in logcat
3. Check that accessibility labels are present on interactive elements

## Common Patterns

### Navigate to a screen
```
launch_app → wait_for_element("Dashboard") → tap_element(text: "Transactions") → wait_for_element("Transactions")
```

### Fill a form
```
tap_element(text: "Email") → input_text("user@example.com") → tap_element(text: "Password") → input_text("secret") → tap_element(text: "Login")
```

### Scroll and find
```
swipe(540, 1500, 540, 500) → find_element(text: "Target Element")
```

### Validate after action
```
tap_element(text: "Save") → wait_for_element("Success") → assert_visible(text: "Goal created") → validate_screen(description: "Goal creation success state")
```
