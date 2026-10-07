// AC-002 / AC-003: React Native bridge to the Kotlin NotificationCaptureService.
// Receives raw notification events via DeviceEventEmitter and forwards them to the
// event uploader pipeline. Android-only; no-ops on other platforms.
import { DeviceEventEmitter, NativeModules, Platform } from 'react-native';
import { registerUserDataCleaner } from '@/state/userData';
import { handleRawNotificationEvent, registerNativeBufferDropper, type RawNotificationEvent } from './eventUploader';

// ── Native module contract (implemented in NotificationBridgeModule.kt) ──────
interface NotificationBridgeNativeModule {
  isPermissionGranted(): Promise<boolean>;
  openNotificationListenerSettings(): void;
  /** Só existe em APK novo (consentimento da captura). Em APK antigo o porteiro é só o JS. */
  setCaptureEnabled?(enabled: boolean): void;
  /** Só em APK novo: o JS deixou de escutar; o nativo guarda os eventos em vez de emiti-los para ninguém. */
  pauseDelivery?(): void;
  /** Só em APK novo: descarta os eventos guardados no nativo (o grupo ativo mudou). */
  discardBuffered?(): void;
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
 * Avisa o serviço nativo se a captura está liberada (aceite do usuário + interruptor ligado). Desligada, o serviço
 * ignora as notificações. No-op em APK sem esse método (a porta de consentimento do JS continua valendo).
 */
export function setNativeCaptureEnabled(enabled: boolean): void {
  if (!isNotificationBridgeAvailable()) return;
  try {
    if (typeof NotificationBridge!.setCaptureEnabled === 'function') {
      NotificationBridge!.setCaptureEnabled(enabled);
    }
  } catch {
    // O porteiro do JS continua valendo.
  }
}

/** Chama um método opcional do módulo nativo; em APK que não o tem (ou se ele falhar) nada acontece. */
function callOptional(method: 'pauseDelivery' | 'discardBuffered'): void {
  if (!isNotificationBridgeAvailable()) return;
  try {
    const fn = NotificationBridge![method];
    if (typeof fn === 'function') fn.call(NotificationBridge);
  } catch {
    // O porteiro do JS continua valendo.
  }
}

/**
 * Descarta o que o nativo guardou sem entregar. Roda sempre que a fila do JS é esvaziada (clearPendingEvents):
 * troca do grupo ativo (o servidor lançaria o evento no grupo do token atual), consentimento retirado, saída.
 */
export function discardNativeBufferedEvents(): void {
  callOptional('discardBuffered');
}

registerNativeBufferDropper(discardNativeBufferedEvents);

// Sair da conta (ou sessão expirada) deixa o serviço nativo desligado: sem usuário, nada de ler notificações.
registerUserDataCleaner(() => setNativeCaptureEnabled(false));

/**
 * Start listening for notification events from the Kotlin service.
 * Automatically parses and uploads each recognised bank notification.
 *
 * Order matters and is fixed by applyCaptureDecision (captureSync.ts): this listener is attached first, and only
 * then is the native side told that capture is enabled, which is when it hands over what it buffered while
 * nobody was listening (app closed, main area unmounted).
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
    // Ninguém escuta mais: o nativo passa a guardar os eventos em vez de emiti-los no vazio.
    callOptional('pauseDelivery');
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
