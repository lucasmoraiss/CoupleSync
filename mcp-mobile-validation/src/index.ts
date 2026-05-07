import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { execFile, spawn } from "node:child_process";
import { promisify } from "node:util";
import * as fs from "node:fs/promises";
import * as path from "node:path";
import * as os from "node:os";

const execFileAsync = promisify(execFile);

// --- Configuration -----------------------------------------------------------

let ADB_PATH = process.env.ADB_PATH || "adb";
const MAESTRO_PATH = process.env.MAESTRO_PATH || "maestro";
const SCREENSHOT_DIR = process.env.SCREENSHOT_DIR || path.join(os.tmpdir(), "mcp-mobile-screenshots");
const APP_PACKAGE = process.env.APP_PACKAGE || "com.couplesync.app";
const MAESTRO_FLOWS_DIR = process.env.MAESTRO_FLOWS_DIR || "";
const PROJECT_ROOT = process.env.PROJECT_ROOT || process.cwd();
const ANDROID_SDK_ROOT = process.env.ANDROID_HOME || process.env.ANDROID_SDK_ROOT || path.join(os.homedir(), "AppData", "Local", "Android", "Sdk");

// Ensure screenshot dir exists
await fs.mkdir(SCREENSHOT_DIR, { recursive: true });

// --- Helpers -----------------------------------------------------------------

async function runAdb(...args: string[]): Promise<{ stdout: string; stderr: string }> {
  try {
    const result = await execFileAsync(ADB_PATH, args, { maxBuffer: 10 * 1024 * 1024 });
    return result;
  } catch (err: any) {
    throw new Error(`ADB command failed: adb ${args.join(" ")}\n${err.stderr || err.message}`);
  }
}

async function runMaestro(...args: string[]): Promise<{ stdout: string; stderr: string }> {
  try {
    const result = await execFileAsync(MAESTRO_PATH, args, {
      maxBuffer: 10 * 1024 * 1024,
      timeout: 120_000,
    });
    return result;
  } catch (err: any) {
    throw new Error(`Maestro command failed: maestro ${args.join(" ")}\n${err.stderr || err.message}`);
  }
}

function truncate(text: string, maxLen: number = 50_000): string {
  if (text.length <= maxLen) return text;
  return text.slice(0, maxLen) + "\n... [truncated]";
}

// --- MCP Server Setup --------------------------------------------------------

const server = new McpServer({
  name: "mobile-validation-mcp",
  version: "1.0.0",
  description:
    "Mobile app validation tools for CoupleSync. Provides ADB-based screen inspection (screenshots, UI hierarchy, element interaction) and Maestro-based flow execution for autonomous agent-driven validation of the Android app on emulator or physical device.",
});

// =============================================================================
// BOOTSTRAP — Self-healing prerequisites check
// =============================================================================

interface PrereqStatus {
  check: string;
  status: "pass" | "fail" | "fixed";
  detail: string;
  action_taken?: string;
}

async function commandExists(cmd: string): Promise<boolean> {
  try {
    const which = os.platform() === "win32" ? "where" : "which";
    await execFileAsync(which, [cmd]);
    return true;
  } catch {
    return false;
  }
}

async function findAdbPath(): Promise<string | null> {
  // Check if adb is already in PATH
  if (await commandExists("adb")) return "adb";

  // Try common Android SDK locations
  const candidates = [
    path.join(ANDROID_SDK_ROOT, "platform-tools", os.platform() === "win32" ? "adb.exe" : "adb"),
    // Fallback for other Windows locations
    path.join("C:", "Android", "Sdk", "platform-tools", "adb.exe"),
    path.join(os.homedir(), "Android", "Sdk", "platform-tools", "adb"),
  ];

  for (const candidate of candidates) {
    try {
      await fs.access(candidate);
      return candidate;
    } catch {
      // not found, continue
    }
  }
  return null;
}

async function findMaestroPath(): Promise<string | null> {
  if (await commandExists("maestro")) return "maestro";

  // Check common install locations
  const candidates = [
    path.join(os.homedir(), ".maestro", "bin", os.platform() === "win32" ? "maestro.bat" : "maestro"),
    path.join(os.homedir(), ".maestro", "bin", "maestro"),
  ];

  for (const candidate of candidates) {
    try {
      await fs.access(candidate);
      return candidate;
    } catch {
      // not found
    }
  }
  return null;
}

async function isDeviceConnected(adbCmd: string): Promise<{ connected: boolean; devices: string }> {
  try {
    const { stdout } = await execFileAsync(adbCmd, ["devices", "-l"], { maxBuffer: 1024 * 1024 });
    const lines = stdout.trim().split("\n").slice(1).filter((l) => l.trim().length > 0);
    const activeDevices = lines.filter((l) => l.includes("device") && !l.includes("offline") && !l.includes("unauthorized"));
    return { connected: activeDevices.length > 0, devices: stdout.trim() };
  } catch {
    return { connected: false, devices: "ADB not reachable" };
  }
}

async function isAppInstalled(adbCmd: string, packageName: string): Promise<boolean> {
  try {
    const { stdout } = await execFileAsync(adbCmd, ["shell", "pm", "list", "packages", packageName]);
    return stdout.includes(packageName);
  } catch {
    return false;
  }
}

async function startEmulatorIfAvailable(): Promise<{ started: boolean; detail: string }> {
  const emulatorPath = path.join(ANDROID_SDK_ROOT, "emulator", os.platform() === "win32" ? "emulator.exe" : "emulator");

  try {
    await fs.access(emulatorPath);
  } catch {
    return { started: false, detail: "Emulator binary not found at: " + emulatorPath };
  }

  // List available AVDs
  try {
    const { stdout } = await execFileAsync(emulatorPath, ["-list-avds"]);
    const avds = stdout.trim().split("\n").filter((l) => l.trim().length > 0);

    if (avds.length === 0) {
      return { started: false, detail: "No AVDs found. Create one in Android Studio → Device Manager." };
    }

    // Launch first available AVD in background
    const avdName = avds[0].trim();
    spawn(emulatorPath, ["-avd", avdName, "-no-snapshot-load"], {
      detached: true,
      stdio: "ignore",
    }).unref();

    return { started: true, detail: `Launched emulator AVD: ${avdName}. It takes 30-60 seconds to boot.` };
  } catch (err: any) {
    return { started: false, detail: `Failed to list/start emulator: ${err.message}` };
  }
}

async function buildAndInstallApp(adbCmd: string): Promise<{ success: boolean; detail: string }> {
  const mobileDir = path.join(PROJECT_ROOT, "mobile");

  // Check if mobile directory exists
  try {
    await fs.access(mobileDir);
  } catch {
    return { success: false, detail: `Mobile directory not found: ${mobileDir}` };
  }

  // Check if node_modules exist
  try {
    await fs.access(path.join(mobileDir, "node_modules"));
  } catch {
    // Install dependencies first
    try {
      await execFileAsync("npm", ["install"], { cwd: mobileDir, maxBuffer: 50 * 1024 * 1024, timeout: 120_000 });
    } catch (err: any) {
      return { success: false, detail: `npm install failed: ${err.message}` };
    }
  }

  // Check if android/ folder exists (prebuild needed)
  try {
    await fs.access(path.join(mobileDir, "android"));
  } catch {
    // Run expo prebuild
    try {
      await execFileAsync("npx", ["expo", "prebuild", "--platform", "android"], {
        cwd: mobileDir,
        maxBuffer: 50 * 1024 * 1024,
        timeout: 300_000,
      });
    } catch (err: any) {
      return { success: false, detail: `expo prebuild failed: ${err.message}` };
    }
  }

  // Build and install using expo run:android
  try {
    await execFileAsync("npx", ["expo", "run:android"], {
      cwd: mobileDir,
      maxBuffer: 50 * 1024 * 1024,
      timeout: 600_000, // 10 minutes for full build
      env: { ...process.env, ANDROID_HOME: ANDROID_SDK_ROOT },
    });
    return { success: true, detail: "App built and installed successfully via expo run:android" };
  } catch (err: any) {
    // Fallback: try installing existing APK if available
    const apkPaths = [
      path.join(mobileDir, "android", "app", "build", "outputs", "apk", "debug", "app-debug.apk"),
      path.join(mobileDir, "android", "app", "build", "outputs", "apk", "release", "app-release.apk"),
    ];

    for (const apk of apkPaths) {
      try {
        await fs.access(apk);
        await execFileAsync(adbCmd, ["install", "-r", apk]);
        return { success: true, detail: `Installed existing APK: ${apk}` };
      } catch {
        // continue
      }
    }

    return { success: false, detail: `Build failed: ${err.message}\nNo pre-built APK found either.` };
  }
}

// --- Tool: ensure_ready ------------------------------------------------------

server.tool(
  "ensure_ready",
  "Self-healing bootstrap: checks ALL prerequisites for mobile validation and attempts to fix them automatically. Checks: (1) ADB available and in PATH, (2) device/emulator connected — if not, starts emulator, (3) CoupleSync app installed — if not, builds and installs it, (4) Maestro available. Call this FIRST before any other validation tool. Returns a detailed status report of what was checked and what actions were taken.",
  {
    install_app_if_missing: z
      .boolean()
      .default(true)
      .describe("If true, builds and installs the app when not found on device. Set false for quick checks."),
    start_emulator_if_no_device: z
      .boolean()
      .default(true)
      .describe("If true, starts an Android emulator when no device is connected."),
    wait_for_device_timeout_ms: z
      .number()
      .default(60_000)
      .describe("How long to wait for a device to become available after starting emulator."),
  },
  async ({ install_app_if_missing, start_emulator_if_no_device, wait_for_device_timeout_ms }) => {
    const results: PrereqStatus[] = [];

    // === CHECK 1: ADB ===
    const foundAdb = await findAdbPath();
    if (foundAdb) {
      ADB_PATH = foundAdb;
      results.push({
        check: "ADB",
        status: foundAdb === "adb" ? "pass" : "fixed",
        detail: `ADB found at: ${foundAdb}`,
        action_taken: foundAdb !== "adb" ? `Updated ADB_PATH to ${foundAdb} (not in system PATH but found in SDK)` : undefined,
      });
    } else {
      results.push({
        check: "ADB",
        status: "fail",
        detail: `ADB not found. Install Android SDK Platform Tools or set ADB_PATH. Expected at: ${path.join(ANDROID_SDK_ROOT, "platform-tools")}`,
      });
      // Can't proceed without ADB
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(
              {
                ready: false,
                results,
                summary: "BLOCKED: ADB not found. Install Android SDK Platform Tools and ensure ANDROID_HOME is set.",
                fix_instructions: [
                  "1. Open Android Studio → SDK Manager → SDK Tools → check 'Android SDK Platform-Tools' → Apply",
                  `2. Add to PATH: ${path.join(ANDROID_SDK_ROOT, "platform-tools")}`,
                  "3. Or set ADB_PATH environment variable in .vscode/mcp.json",
                ],
              },
              null,
              2
            ),
          },
        ],
      };
    }

    // === CHECK 2: DEVICE CONNECTED ===
    let deviceStatus = await isDeviceConnected(ADB_PATH);

    if (!deviceStatus.connected && start_emulator_if_no_device) {
      const emulatorResult = await startEmulatorIfAvailable();
      results.push({
        check: "Emulator Launch",
        status: emulatorResult.started ? "fixed" : "fail",
        detail: emulatorResult.detail,
        action_taken: emulatorResult.started ? "Started Android emulator automatically" : undefined,
      });

      if (emulatorResult.started) {
        // Wait for device to come online
        const startWait = Date.now();
        while (Date.now() - startWait < wait_for_device_timeout_ms) {
          await new Promise((r) => setTimeout(r, 3_000));
          deviceStatus = await isDeviceConnected(ADB_PATH);
          if (deviceStatus.connected) break;
        }
      }
    }

    if (deviceStatus.connected) {
      results.push({
        check: "Device/Emulator",
        status: "pass",
        detail: deviceStatus.devices,
      });
    } else {
      results.push({
        check: "Device/Emulator",
        status: "fail",
        detail: "No device connected. Connect USB device with debugging enabled or start an emulator from Android Studio.",
      });
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify(
              {
                ready: false,
                results,
                summary: "BLOCKED: No Android device or emulator available.",
                fix_instructions: [
                  "Option A: Connect a USB device with 'USB Debugging' enabled (Settings → Developer Options)",
                  "Option B: Open Android Studio → Device Manager → Start an AVD",
                  "Then re-run ensure_ready",
                ],
              },
              null,
              2
            ),
          },
        ],
      };
    }

    // === CHECK 3: APP INSTALLED ===
    const appInstalled = await isAppInstalled(ADB_PATH, APP_PACKAGE);

    if (appInstalled) {
      results.push({
        check: "App Installed",
        status: "pass",
        detail: `${APP_PACKAGE} is installed on device`,
      });
    } else if (install_app_if_missing) {
      const installResult = await buildAndInstallApp(ADB_PATH);
      results.push({
        check: "App Installed",
        status: installResult.success ? "fixed" : "fail",
        detail: installResult.detail,
        action_taken: installResult.success ? "Built and installed app automatically" : undefined,
      });
    } else {
      results.push({
        check: "App Installed",
        status: "fail",
        detail: `${APP_PACKAGE} not installed. Run with install_app_if_missing=true or manually: cd mobile && npx expo run:android`,
      });
    }

    // === CHECK 4: MAESTRO (optional) ===
    const maestroPath = await findMaestroPath();
    if (maestroPath) {
      results.push({
        check: "Maestro",
        status: "pass",
        detail: `Maestro found at: ${maestroPath}`,
      });
    } else {
      results.push({
        check: "Maestro",
        status: "fail",
        detail: "Maestro not installed. Flow-based validation won't work. Install: curl -Ls 'https://get.maestro.mobile.dev' | bash",
      });
    }

    // === SUMMARY ===
    const allPass = results.every((r) => r.status === "pass" || r.status === "fixed");
    const hasFailures = results.some((r) => r.status === "fail");
    const criticalFail = results.some(
      (r) => r.status === "fail" && (r.check === "ADB" || r.check === "Device/Emulator")
    );

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify(
            {
              ready: !criticalFail && !hasFailures,
              ready_for_adb: !criticalFail,
              ready_for_maestro: !!maestroPath,
              results,
              summary: allPass
                ? "ALL PREREQUISITES MET. Ready for mobile validation."
                : criticalFail
                  ? "BLOCKED: Critical prerequisites missing (ADB or device)."
                  : "PARTIAL: ADB validation ready but some optional tools missing.",
            },
            null,
            2
          ),
        },
      ],
    };
  }
);

// =============================================================================
// ADB TOOLS — Low-level device interaction
// =============================================================================

// --- Tool: list_devices ------------------------------------------------------

server.tool(
  "list_devices",
  "List connected Android devices and emulators. Use this first to verify a device is available before running other validation tools.",
  {},
  async () => {
    const { stdout } = await runAdb("devices", "-l");
    return { content: [{ type: "text", text: stdout.trim() }] };
  }
);

// --- Tool: take_screenshot ---------------------------------------------------

server.tool(
  "take_screenshot",
  "Capture a screenshot of the current device screen. Returns the file path to the saved PNG. Use this to visually validate the current state of the app after navigation or interaction. The screenshot can be analyzed with vision capabilities.",
  {
    filename: z
      .string()
      .optional()
      .describe("Optional filename (without extension). Defaults to timestamp-based name."),
  },
  async ({ filename }) => {
    const fname = filename || `screenshot_${Date.now()}`;
    const devicePath = `/sdcard/${fname}.png`;
    const localPath = path.join(SCREENSHOT_DIR, `${fname}.png`);

    await runAdb("shell", "screencap", "-p", devicePath);
    await runAdb("pull", devicePath, localPath);
    await runAdb("shell", "rm", devicePath);

    // Read file as base64 for inline return
    const imageBuffer = await fs.readFile(localPath);
    const base64 = imageBuffer.toString("base64");

    return {
      content: [
        { type: "text", text: `Screenshot saved to: ${localPath}` },
        {
          type: "image",
          data: base64,
          mimeType: "image/png",
        },
      ],
    };
  }
);

// --- Tool: get_ui_hierarchy --------------------------------------------------

server.tool(
  "get_ui_hierarchy",
  "Dump the current UI hierarchy (accessibility tree) as XML. This provides structured information about all visible elements: their text content, resource IDs, class names, bounds, clickable/focusable states, and content descriptions. Essential for finding elements to interact with or asserting expected UI state.",
  {},
  async () => {
    const devicePath = "/sdcard/ui_dump.xml";
    await runAdb("shell", "uiautomator", "dump", devicePath);
    const { stdout } = await runAdb("shell", "cat", devicePath);
    await runAdb("shell", "rm", devicePath);

    return { content: [{ type: "text", text: truncate(stdout) }] };
  }
);

// --- Tool: find_element ------------------------------------------------------

server.tool(
  "find_element",
  "Search the UI hierarchy for elements matching a query. Returns matching elements with their bounds, text, resource-id, and class. Use this to locate elements before tapping or asserting visibility.",
  {
    text: z.string().optional().describe("Text content to search for (partial match)."),
    resource_id: z.string().optional().describe("Resource ID to search for (partial match)."),
    content_desc: z.string().optional().describe("Content description / accessibility label to search for."),
    class_name: z.string().optional().describe("Class name filter (e.g., 'android.widget.Button')."),
  },
  async ({ text, resource_id, content_desc, class_name }) => {
    if (!text && !resource_id && !content_desc && !class_name) {
      return { content: [{ type: "text", text: "Error: At least one search parameter is required." }] };
    }

    const devicePath = "/sdcard/ui_dump.xml";
    await runAdb("shell", "uiautomator", "dump", devicePath);
    const { stdout } = await runAdb("shell", "cat", devicePath);
    await runAdb("shell", "rm", devicePath);

    // Parse XML to find matching nodes
    const nodeRegex = /<node[^>]*>/g;
    const matches: string[] = [];
    let match: RegExpExecArray | null;

    while ((match = nodeRegex.exec(stdout)) !== null) {
      const node = match[0];
      let matched = true;

      if (text && !node.includes(`text="${text}"`)) {
        // Try partial match
        const textMatch = node.match(/text="([^"]*)"/);
        if (!textMatch || !textMatch[1].toLowerCase().includes(text.toLowerCase())) {
          matched = false;
        }
      }
      if (resource_id && !node.toLowerCase().includes(resource_id.toLowerCase())) {
        matched = false;
      }
      if (content_desc) {
        const descMatch = node.match(/content-desc="([^"]*)"/);
        if (!descMatch || !descMatch[1].toLowerCase().includes(content_desc.toLowerCase())) {
          matched = false;
        }
      }
      if (class_name && !node.includes(`class="${class_name}"`)) {
        matched = false;
      }

      if (matched) {
        matches.push(node);
      }
    }

    if (matches.length === 0) {
      return { content: [{ type: "text", text: "No elements found matching the query." }] };
    }

    const summary = matches.map((m, i) => {
      const getText = m.match(/text="([^"]*)"/)?.[1] || "";
      const getId = m.match(/resource-id="([^"]*)"/)?.[1] || "";
      const getBounds = m.match(/bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"/);
      const getDesc = m.match(/content-desc="([^"]*)"/)?.[1] || "";
      const getClass = m.match(/class="([^"]*)"/)?.[1] || "";

      return `[${i}] class="${getClass}" text="${getText}" id="${getId}" desc="${getDesc}" bounds=${getBounds ? `[${getBounds[1]},${getBounds[2]}][${getBounds[3]},${getBounds[4]}]` : "unknown"}`;
    });

    return { content: [{ type: "text", text: `Found ${matches.length} element(s):\n${summary.join("\n")}` }] };
  }
);

// --- Tool: tap_element -------------------------------------------------------

server.tool(
  "tap_element",
  "Tap on a screen element. Provide either coordinates (x, y) or text/description to find and tap. Use after find_element to interact with specific UI elements.",
  {
    x: z.number().optional().describe("X coordinate to tap."),
    y: z.number().optional().describe("Y coordinate to tap."),
    text: z.string().optional().describe("Find element by text and tap its center."),
    content_desc: z.string().optional().describe("Find element by accessibility label and tap its center."),
  },
  async ({ x, y, text, content_desc }) => {
    if (x !== undefined && y !== undefined) {
      await runAdb("shell", "input", "tap", String(x), String(y));
      return { content: [{ type: "text", text: `Tapped at (${x}, ${y})` }] };
    }

    if (!text && !content_desc) {
      return { content: [{ type: "text", text: "Error: Provide (x, y) coordinates or text/content_desc to find element." }] };
    }

    // Find element bounds first
    const devicePath = "/sdcard/ui_dump.xml";
    await runAdb("shell", "uiautomator", "dump", devicePath);
    const { stdout } = await runAdb("shell", "cat", devicePath);
    await runAdb("shell", "rm", devicePath);

    const nodeRegex = /<node[^>]*>/g;
    let match: RegExpExecArray | null;

    while ((match = nodeRegex.exec(stdout)) !== null) {
      const node = match[0];
      let found = false;

      if (text) {
        const textMatch = node.match(/text="([^"]*)"/);
        if (textMatch && textMatch[1].toLowerCase().includes(text.toLowerCase())) found = true;
      }
      if (content_desc) {
        const descMatch = node.match(/content-desc="([^"]*)"/);
        if (descMatch && descMatch[1].toLowerCase().includes(content_desc.toLowerCase())) found = true;
      }

      if (found) {
        const boundsMatch = node.match(/bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"/);
        if (boundsMatch) {
          const cx = Math.round((parseInt(boundsMatch[1]) + parseInt(boundsMatch[3])) / 2);
          const cy = Math.round((parseInt(boundsMatch[2]) + parseInt(boundsMatch[4])) / 2);
          await runAdb("shell", "input", "tap", String(cx), String(cy));
          return { content: [{ type: "text", text: `Tapped element at center (${cx}, ${cy})` }] };
        }
      }
    }

    return { content: [{ type: "text", text: "Error: Could not find element to tap." }] };
  }
);

// --- Tool: input_text --------------------------------------------------------

server.tool(
  "input_text",
  "Type text into the currently focused input field. Use after tapping a text input element.",
  {
    text: z.string().describe("The text to type. Special characters may need escaping."),
    clear_first: z.boolean().default(false).describe("If true, selects all and deletes before typing."),
  },
  async ({ text, clear_first }) => {
    if (clear_first) {
      // Select all + delete
      await runAdb("shell", "input", "keyevent", "KEYCODE_MOVE_HOME");
      await runAdb("shell", "input", "keyevent", "--longpress", "KEYCODE_MOVE_END");
      await runAdb("shell", "input", "keyevent", "KEYCODE_DEL");
    }

    // Escape special characters for adb shell input
    const escaped = text.replace(/ /g, "%s").replace(/'/g, "\\'").replace(/"/g, '\\"');
    await runAdb("shell", "input", "text", escaped);

    return { content: [{ type: "text", text: `Typed: "${text}"` }] };
  }
);

// --- Tool: swipe -------------------------------------------------------------

server.tool(
  "swipe",
  "Perform a swipe gesture on the device screen. Useful for scrolling, pull-to-refresh, or navigating between pages.",
  {
    start_x: z.number().describe("Starting X coordinate."),
    start_y: z.number().describe("Starting Y coordinate."),
    end_x: z.number().describe("Ending X coordinate."),
    end_y: z.number().describe("Ending Y coordinate."),
    duration_ms: z.number().default(300).describe("Duration of swipe in milliseconds."),
  },
  async ({ start_x, start_y, end_x, end_y, duration_ms }) => {
    await runAdb("shell", "input", "swipe", String(start_x), String(start_y), String(end_x), String(end_y), String(duration_ms));
    return { content: [{ type: "text", text: `Swiped from (${start_x},${start_y}) to (${end_x},${end_y}) over ${duration_ms}ms` }] };
  }
);

// --- Tool: press_key ---------------------------------------------------------

server.tool(
  "press_key",
  "Press a hardware/software key on the device. Common keys: KEYCODE_BACK (back), KEYCODE_HOME (home), KEYCODE_ENTER (confirm), KEYCODE_TAB (next field).",
  {
    keycode: z.string().describe("Android keycode (e.g., 'KEYCODE_BACK', 'KEYCODE_HOME', 'KEYCODE_ENTER')."),
  },
  async ({ keycode }) => {
    await runAdb("shell", "input", "keyevent", keycode);
    return { content: [{ type: "text", text: `Pressed key: ${keycode}` }] };
  }
);

// --- Tool: launch_app --------------------------------------------------------

server.tool(
  "launch_app",
  "Launch the CoupleSync app (or another app by package name). Clears the app from recent tasks first for a fresh start if requested.",
  {
    package_name: z.string().default(APP_PACKAGE).describe("Android package name to launch."),
    clear_data: z.boolean().default(false).describe("If true, clears app data before launching (factory reset)."),
    fresh: z.boolean().default(false).describe("If true, force-stops and restarts the app."),
  },
  async ({ package_name, clear_data, fresh }) => {
    if (clear_data) {
      await runAdb("shell", "pm", "clear", package_name);
    }
    if (fresh) {
      await runAdb("shell", "am", "force-stop", package_name);
    }

    // Launch main activity via monkey (works without knowing activity name)
    await runAdb("shell", "monkey", "-p", package_name, "-c", "android.intent.category.LAUNCHER", "1");

    return { content: [{ type: "text", text: `Launched ${package_name}${clear_data ? " (data cleared)" : ""}${fresh ? " (fresh start)" : ""}` }] };
  }
);

// --- Tool: get_app_state -----------------------------------------------------

server.tool(
  "get_app_state",
  "Check if the CoupleSync app is currently running and in foreground. Returns the current activity and app process state.",
  {
    package_name: z.string().default(APP_PACKAGE).describe("Android package name to check."),
  },
  async ({ package_name }) => {
    // Check current foreground activity
    const { stdout: activityOut } = await runAdb("shell", "dumpsys", "activity", "activities", "|", "grep", "mResumedActivity");

    // Check if process is running
    const { stdout: psOut } = await runAdb("shell", "pidof", package_name);

    const isRunning = psOut.trim().length > 0;
    const isForeground = activityOut.includes(package_name);

    return {
      content: [
        {
          type: "text",
          text: JSON.stringify(
            {
              package: package_name,
              is_running: isRunning,
              is_foreground: isForeground,
              pid: psOut.trim() || null,
              current_activity: activityOut.trim() || "none",
            },
            null,
            2
          ),
        },
      ],
    };
  }
);

// --- Tool: get_screen_info ---------------------------------------------------

server.tool(
  "get_screen_info",
  "Get device screen dimensions and density. Useful for calculating tap coordinates and understanding layout constraints.",
  {},
  async () => {
    const { stdout: sizeOut } = await runAdb("shell", "wm", "size");
    const { stdout: densityOut } = await runAdb("shell", "wm", "density");

    return {
      content: [{ type: "text", text: `${sizeOut.trim()}\n${densityOut.trim()}` }],
    };
  }
);

// --- Tool: get_logcat --------------------------------------------------------

server.tool(
  "get_logcat",
  "Read recent logcat output filtered by tag or package. Useful for checking app errors, React Native JS exceptions, or network issues after an interaction.",
  {
    tag: z.string().optional().describe("Logcat tag filter (e.g., 'ReactNativeJS', 'CoupleSync')."),
    lines: z.number().default(50).describe("Number of recent lines to fetch."),
    level: z
      .enum(["V", "D", "I", "W", "E", "F"])
      .default("I")
      .describe("Minimum log level: V(erbose), D(ebug), I(nfo), W(arn), E(rror), F(atal)."),
  },
  async ({ tag, lines, level }) => {
    const args = ["shell", "logcat", "-d", "-t", String(lines), `*:${level}`];
    if (tag) {
      args.push("-s", `${tag}:${level}`);
    }
    const { stdout } = await runAdb(...args);
    return { content: [{ type: "text", text: truncate(stdout) }] };
  }
);

// --- Tool: assert_visible ----------------------------------------------------

server.tool(
  "assert_visible",
  "Assert that text or an element is currently visible on screen. Returns pass/fail with details. Use this for validation checks after navigation or actions.",
  {
    text: z.string().optional().describe("Text that should be visible on screen."),
    content_desc: z.string().optional().describe("Accessibility label that should be present."),
    resource_id: z.string().optional().describe("Resource ID that should be present."),
    should_exist: z.boolean().default(true).describe("If false, asserts the element is NOT visible."),
  },
  async ({ text, content_desc, resource_id, should_exist }) => {
    if (!text && !content_desc && !resource_id) {
      return { content: [{ type: "text", text: "Error: At least one assertion parameter required." }] };
    }

    const devicePath = "/sdcard/ui_dump.xml";
    await runAdb("shell", "uiautomator", "dump", devicePath);
    const { stdout } = await runAdb("shell", "cat", devicePath);
    await runAdb("shell", "rm", devicePath);

    let found = false;
    let detail = "";

    if (text) {
      found = stdout.toLowerCase().includes(`text="${text.toLowerCase()}"`) || stdout.toLowerCase().includes(text.toLowerCase());
      detail = `text="${text}"`;
    } else if (content_desc) {
      found = stdout.toLowerCase().includes(content_desc.toLowerCase());
      detail = `content_desc="${content_desc}"`;
    } else if (resource_id) {
      found = stdout.toLowerCase().includes(resource_id.toLowerCase());
      detail = `resource_id="${resource_id}"`;
    }

    const passed = should_exist ? found : !found;
    const status = passed ? "PASS" : "FAIL";
    const expectation = should_exist ? "should be visible" : "should NOT be visible";

    return {
      content: [
        {
          type: "text",
          text: `${status}: ${detail} ${expectation}.\nActual: ${found ? "FOUND" : "NOT FOUND"} in UI hierarchy.`,
        },
      ],
    };
  }
);

// =============================================================================
// MAESTRO TOOLS — High-level flow execution
// =============================================================================

// --- Tool: run_maestro_flow --------------------------------------------------

server.tool(
  "run_maestro_flow",
  "Execute a Maestro test flow YAML file. Maestro provides reliable, deterministic mobile UI testing. Returns pass/fail with detailed step results. Use for complex multi-step validations.",
  {
    flow_file: z.string().describe("Path to the Maestro flow YAML file (absolute or relative to MAESTRO_FLOWS_DIR)."),
    env: z.record(z.string(), z.string()).optional().describe("Environment variables to pass to the flow."),
  },
  async ({ flow_file, env }) => {
    const resolvedPath = path.isAbsolute(flow_file)
      ? flow_file
      : path.join(MAESTRO_FLOWS_DIR, flow_file);

    // Verify file exists
    try {
      await fs.access(resolvedPath);
    } catch {
      return { content: [{ type: "text", text: `Error: Flow file not found: ${resolvedPath}` }] };
    }

    const args = ["test", resolvedPath];
    if (env) {
      for (const [key, value] of Object.entries(env)) {
        args.push("-e", `${key}=${value}`);
      }
    }

    try {
      const { stdout, stderr } = await runMaestro(...args);
      return {
        content: [
          { type: "text", text: `PASS: Flow completed successfully.\n\n${truncate(stdout)}${stderr ? `\nStderr: ${stderr}` : ""}` },
        ],
      };
    } catch (err: any) {
      return {
        content: [
          { type: "text", text: `FAIL: Flow execution failed.\n\n${err.message}` },
        ],
      };
    }
  }
);

// --- Tool: run_maestro_command ------------------------------------------------

server.tool(
  "run_maestro_command",
  "Execute a single Maestro command inline (without a flow file). Useful for quick one-off validations like asserting text or tapping elements using Maestro's reliable element matching.",
  {
    commands: z
      .array(z.string())
      .describe("Array of Maestro YAML commands to execute inline (e.g., ['- assertVisible: \"Dashboard\"', '- tapOn: \"Settings\"'])."),
  },
  async ({ commands }) => {
    // Write temporary flow file
    const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "maestro-"));
    const tempFlow = path.join(tempDir, "inline_flow.yaml");
    const flowContent = `appId: ${APP_PACKAGE}\n---\n${commands.join("\n")}`;
    await fs.writeFile(tempFlow, flowContent, "utf-8");

    try {
      const { stdout, stderr } = await runMaestro("test", tempFlow);
      return {
        content: [
          { type: "text", text: `PASS: Commands executed successfully.\n\n${truncate(stdout)}${stderr ? `\nStderr: ${stderr}` : ""}` },
        ],
      };
    } catch (err: any) {
      return {
        content: [{ type: "text", text: `FAIL: Command execution failed.\n\n${err.message}` }],
      };
    } finally {
      await fs.rm(tempDir, { recursive: true, force: true });
    }
  }
);

// --- Tool: list_maestro_flows ------------------------------------------------

server.tool(
  "list_maestro_flows",
  "List available Maestro flow files in the project's flows directory. Use to discover pre-built validation flows.",
  {},
  async () => {
    if (!MAESTRO_FLOWS_DIR) {
      return { content: [{ type: "text", text: "MAESTRO_FLOWS_DIR not configured. Set the environment variable to the flows directory path." }] };
    }

    try {
      const entries = await fs.readdir(MAESTRO_FLOWS_DIR, { recursive: true });
      const flows = entries.filter((e) => String(e).endsWith(".yaml") || String(e).endsWith(".yml"));
      return { content: [{ type: "text", text: flows.length > 0 ? flows.join("\n") : "No flow files found." }] };
    } catch {
      return { content: [{ type: "text", text: `Error: Cannot read flows directory: ${MAESTRO_FLOWS_DIR}` }] };
    }
  }
);

// --- Tool: validate_screen ---------------------------------------------------

server.tool(
  "validate_screen",
  "High-level screen validation. Takes a screenshot AND dumps the UI hierarchy in one call, returning both. This is the primary tool for agents to 'see' the current app state — combining visual evidence with structured element data.",
  {
    description: z.string().optional().describe("What you expect to see on this screen (for documentation/logging)."),
    filename: z.string().optional().describe("Screenshot filename prefix."),
  },
  async ({ description, filename }) => {
    const fname = filename || `validate_${Date.now()}`;
    const devicePath = `/sdcard/${fname}.png`;
    const localPath = path.join(SCREENSHOT_DIR, `${fname}.png`);

    // Screenshot
    await runAdb("shell", "screencap", "-p", devicePath);
    await runAdb("pull", devicePath, localPath);
    await runAdb("shell", "rm", devicePath);

    // UI hierarchy
    const dumpPath = "/sdcard/ui_dump.xml";
    await runAdb("shell", "uiautomator", "dump", dumpPath);
    const { stdout: hierarchy } = await runAdb("shell", "cat", dumpPath);
    await runAdb("shell", "rm", dumpPath);

    // Read screenshot as base64
    const imageBuffer = await fs.readFile(localPath);
    const base64 = imageBuffer.toString("base64");

    // Extract visible text elements for quick summary
    const textRegex = /text="([^"]+)"/g;
    const visibleTexts: string[] = [];
    let textMatch: RegExpExecArray | null;
    while ((textMatch = textRegex.exec(hierarchy)) !== null) {
      if (textMatch[1].trim()) visibleTexts.push(textMatch[1]);
    }

    const summary = [
      description ? `Expected: ${description}` : "",
      `Screenshot: ${localPath}`,
      `\nVisible text elements (${visibleTexts.length}):`,
      visibleTexts.slice(0, 30).join(" | "),
      visibleTexts.length > 30 ? `... and ${visibleTexts.length - 30} more` : "",
      `\nFull UI Hierarchy:\n${truncate(hierarchy, 30_000)}`,
    ]
      .filter(Boolean)
      .join("\n");

    return {
      content: [
        { type: "text", text: summary },
        { type: "image", data: base64, mimeType: "image/png" },
      ],
    };
  }
);

// --- Tool: wait_for_element --------------------------------------------------

server.tool(
  "wait_for_element",
  "Wait for an element to appear on screen (polls UI hierarchy). Useful after navigation or async operations. Times out after the specified duration.",
  {
    text: z.string().optional().describe("Text to wait for."),
    content_desc: z.string().optional().describe("Accessibility label to wait for."),
    resource_id: z.string().optional().describe("Resource ID to wait for."),
    timeout_ms: z.number().default(10_000).describe("Maximum time to wait in milliseconds."),
    poll_interval_ms: z.number().default(1_000).describe("Interval between UI hierarchy checks."),
  },
  async ({ text, content_desc, resource_id, timeout_ms, poll_interval_ms }) => {
    if (!text && !content_desc && !resource_id) {
      return { content: [{ type: "text", text: "Error: At least one search parameter required." }] };
    }

    const startTime = Date.now();
    const searchTerm = (text || content_desc || resource_id || "").toLowerCase();

    while (Date.now() - startTime < timeout_ms) {
      const devicePath = "/sdcard/ui_dump.xml";
      try {
        await runAdb("shell", "uiautomator", "dump", devicePath);
        const { stdout } = await runAdb("shell", "cat", devicePath);
        await runAdb("shell", "rm", devicePath);

        if (stdout.toLowerCase().includes(searchTerm)) {
          const elapsed = Date.now() - startTime;
          return { content: [{ type: "text", text: `FOUND: Element appeared after ${elapsed}ms.` }] };
        }
      } catch {
        // Ignore transient ADB errors during polling
      }

      // Wait before next poll
      await new Promise((resolve) => setTimeout(resolve, poll_interval_ms));
    }

    return { content: [{ type: "text", text: `TIMEOUT: Element not found within ${timeout_ms}ms. Searched for: "${searchTerm}"` }] };
  }
);

// =============================================================================
// SERVER STARTUP
// =============================================================================

const transport = new StdioServerTransport();
await server.connect(transport);
