// AC-002 / AC-003: React Native bridge to the Kotlin NotificationCaptureService.
// Receives raw notification events via DeviceEventEmitter and forwards them to the
// event uploader pipeline. Android-only; no-ops on other platforms.
import { DeviceEventEmitter, NativeModules, Platform } from 'react-native';
import { handleRawNotificationEvent, type RawNotificationEvent } from './eventUploader';

// ── Native module contract (implemented in NotificationBridgeModule.kt) ──────
interface NotificationBridgeNativeModule {
  isPermissionGranted(): Promise<boolean>;
  openNotificationListenerSettings(): void;
}

const NotificationBridge: NotificationBridgeNativeModule | undefined =
  NativeModules.NotificationBridge as NotificationBridgeNativeModule | undefined;

// ── Event name emitted by NotificationBridgeModule.kt ────────────────────────
const NATIVE_EVENT_NAME = 'NotificationCaptured';

// ── Public API ────────────────────────────────────────────────────────────────

/**
 * Returns true when the native NotificationBridge module is available.
 * Will be false on iOS, in Expo Go, and before expo prebuild has been run.
 */
export function isNotificationBridgeAvailable(): boolean {
  return Platform.OS === 'android' && NotificationBridge != null;
}

/**
 * Check whether the user has granted the Notification Listener permission via
 * Android Settings → Notification Access.
 */
export async function checkNotificationListenerPermission(): Promise<boolean> {
  if (!isNotificationBridgeAvailable()) return false;
  try {
    return await NotificationBridge!.isPermissionGranted();
  } catch {
    return false;
  }
}

/**
 * Open the Android system screen where the user can grant/revoke the
 * Notification Listener permission. Safe to call on any platform.
 */
export function openNotificationListenerSettings(): void {
  if (!isNotificationBridgeAvailable()) return;
  NotificationBridge!.openNotificationListenerSettings();
}

/**
 * Start listening for notification events from the Kotlin service.
 * Automatically parses and uploads each recognised bank notification.
 *
 * @returns Cleanup function — call it (e.g. in useEffect return) to unsubscribe.
 */
export function startNotificationCapture(): () => void {
  if (!isNotificationBridgeAvailable()) {
    return () => {};
  }

  const subscription = DeviceEventEmitter.addListener(
    NATIVE_EVENT_NAME,
    (event: RawNotificationEvent) => {
      // Fire-and-forget; uploader handles queuing on failure (AC-009)
      void handleRawNotificationEvent(event);
    },
  );

  return () => {
    subscription.remove();
  };
}

/**
 * One-shot raw event listener for testing or manual integration diagnostics.
 * Does NOT auto-upload — returns the raw event payload.
 */
export function addRawNotificationListener(
  handler: (event: RawNotificationEvent) => void,
): { remove: () => void } {
  if (Platform.OS !== 'android') return { remove: () => {} };

  const subscription = DeviceEventEmitter.addListener(NATIVE_EVENT_NAME, handler);
  return { remove: () => subscription.remove() };
}
