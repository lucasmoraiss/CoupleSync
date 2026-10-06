// Liga a captura de notificações ao consentimento do usuário logado. Montado uma vez no layout principal.
// A decisão está em captureSync.ts (pura, testada); aqui só se executa o resultado:
//  - carrega o consentimento do usuário que está logado;
//  - habilita/desabilita o serviço nativo e a escuta conforme a decisão (nunca desabilita só por não ter carregado);
//  - ao desmontar (logout, expiração) o nativo é desabilitado; sair da conta também o desabilita por um limpador
//    registrado em NotificationListenerBridge.ts;
//  - para quem já usa a captura e nunca respondeu, abre a tela de consentimento UMA vez (registra que abriu).
import { useEffect, useState } from 'react';
import { Platform } from 'react-native';
import { router } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import { useConsentStore } from '@/modules/privacy/consentStore';
import {
  checkNotificationListenerPermission,
  isNotificationBridgeAvailable,
  setNativeCaptureEnabled,
  startNotificationCapture,
} from './NotificationListenerBridge';
import { clearPendingEvents } from './eventUploader';
import { decideCaptureSync } from './captureSync';

export const CAPTURE_CONSENT_ROUTE = '/(main)/settings/capture-consent';

export function useCaptureConsentSync(): void {
  const sessionUserId = useSessionStore((s) => s.userId);
  const loadConsent = useConsentStore((s) => s.load);
  const consentUserId = useConsentStore((s) => s.userId);
  const loaded = useConsentStore((s) => s.loaded);
  const record = useConsentStore((s) => s.record);
  const [permission, setPermission] = useState<boolean | null>(null);

  const decision = decideCaptureSync({
    sessionUserId,
    consentUserId,
    loaded,
    record,
    listenerPermissionGranted: permission,
  });

  useEffect(() => {
    if (sessionUserId) void loadConsent(sessionUserId);
  }, [sessionUserId, loadConsent]);

  // A permissão só interessa para decidir abrir a tela de consentimento.
  const needsPermissionCheck = loaded && record.capture.decidedAt === null && record.capture.promptShownAt === null;
  useEffect(() => {
    if (!needsPermissionCheck || Platform.OS !== 'android' || !isNotificationBridgeAvailable()) return;
    let cancelled = false;
    checkNotificationListenerPermission().then((granted) => {
      if (!cancelled) setPermission(granted);
    });
    return () => {
      cancelled = true;
    };
  }, [needsPermissionCheck, sessionUserId]);

  useEffect(() => {
    if (decision.native === 'enable') setNativeCaptureEnabled(true);
    else if (decision.native === 'disable') setNativeCaptureEnabled(false);
    if (decision.clearPending) clearPendingEvents(); // consentimento retirado ou ainda não dado
    if (!decision.listen) return;
    return startNotificationCapture();
  }, [decision.native, decision.listen, decision.clearPending]);

  useEffect(() => {
    if (!decision.prompt) return;
    void useConsentStore.getState().markCapturePromptShown().then((recorded) => {
      if (recorded) router.push(CAPTURE_CONSENT_ROUTE as any);
    });
  }, [decision.prompt]);

  // Saiu do app logado (logout, sessão expirada): o serviço nativo para de ler notificações.
  useEffect(() => () => setNativeCaptureEnabled(false), []);
}
