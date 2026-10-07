// Liga a captura de notificações ao consentimento do usuário logado. Montado uma vez no layout principal.
// A decisão está em captureSync.ts (pura, testada); aqui só se executa o resultado:
//  - carrega o consentimento do usuário que está logado;
//  - habilita/desabilita o serviço nativo e a escuta conforme a decisão (nunca desabilita só por não ter carregado);
//  - ao desmontar NÃO desabilita o nativo: o layout principal sai de cena também sem logout (ir criar ou entrar em
//    outro grupo, o Android destruir a tela). Sair da conta desabilita pelo limpador registrado em
//    NotificationListenerBridge.ts, e a sessão vazia pela decisão `disable`;
//  - se a leitura do consentimento falha, tenta de novo depois (até lá o nativo fica como está e nada é perguntado);
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
import { applyCaptureDecision, decideCaptureSync } from './captureSync';

export const CAPTURE_CONSENT_ROUTE = '/(main)/settings/capture-consent';

/** Espera antes de tentar ler de novo o consentimento quando o armazenamento seguro falhou. */
const CONSENT_RELOAD_DELAY_MS = 5_000;

export function useCaptureConsentSync(): void {
  const sessionUserId = useSessionStore((s) => s.userId);
  const loadConsent = useConsentStore((s) => s.load);
  const consentUserId = useConsentStore((s) => s.userId);
  const loaded = useConsentStore((s) => s.loaded);
  const loadFailed = useConsentStore((s) => s.loadFailed);
  const loadAttempts = useConsentStore((s) => s.loadAttempts);
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

  // A leitura falhou: não se sabe a resposta do usuário. Tenta de novo; cada falha agenda a próxima tentativa.
  useEffect(() => {
    if (!sessionUserId || !loadFailed) return;
    const timer = setTimeout(() => void loadConsent(sessionUserId), CONSENT_RELOAD_DELAY_MS);
    return () => clearTimeout(timer);
  }, [sessionUserId, loadFailed, loadAttempts, loadConsent]);

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

  useEffect(
    () =>
      applyCaptureDecision(
        { native: decision.native, listen: decision.listen, clearPending: decision.clearPending, prompt: false },
        {
          attachListener: startNotificationCapture,
          setNativeEnabled: setNativeCaptureEnabled,
          clearPending: clearPendingEvents, // consentimento retirado ou ainda não dado
        },
      ),
    [decision.native, decision.listen, decision.clearPending],
  );

  useEffect(() => {
    if (!decision.prompt) return;
    void useConsentStore.getState().markCapturePromptShown().then((recorded) => {
      if (recorded) router.push(CAPTURE_CONSENT_ROUTE as any);
    });
  }, [decision.prompt]);
}
