// Liga a captura de notificações ao consentimento do usuário logado. Montado uma vez no layout principal.
//  - carrega o consentimento do usuário que está logado;
//  - só escuta o módulo nativo (e libera o serviço nativo) enquanto há aceite E o interruptor está ligado;
//  - ao desligar (ou trocar de usuário), para de escutar e descarta o que estava pendente;
//  - para quem já usa a captura (permissão concedida) e nunca respondeu, abre a tela de consentimento uma vez.
import { useEffect, useRef } from 'react';
import { Platform } from 'react-native';
import { router } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { isCaptureAllowed, shouldPromptCaptureConsent } from '@/modules/privacy/consent';
import {
  checkNotificationListenerPermission,
  isNotificationBridgeAvailable,
  setNativeCaptureEnabled,
  startNotificationCapture,
} from './NotificationListenerBridge';
import { clearPendingEvents } from './eventUploader';

export const CAPTURE_CONSENT_ROUTE = '/(main)/settings/capture-consent';

export function useCaptureConsentSync(): void {
  const userId = useSessionStore((s) => s.userId);
  const loadConsent = useConsentStore((s) => s.load);
  const loaded = useConsentStore((s) => s.loaded && s.userId === userId);
  const record = useConsentStore((s) => s.record);
  const allowed = loaded && isCaptureAllowed(record);
  const needsPromptCheck = loaded && record.capture.decidedAt === null;
  const promptedForUser = useRef<string | null>(null);

  useEffect(() => {
    if (userId) void loadConsent(userId);
  }, [userId, loadConsent]);

  useEffect(() => {
    setNativeCaptureEnabled(allowed);
    if (!allowed) {
      clearPendingEvents(); // consentimento retirado ou ainda não dado: nada pendente sai do aparelho
      return;
    }
    return startNotificationCapture();
  }, [allowed]);

  useEffect(() => {
    if (!userId || !needsPromptCheck || promptedForUser.current === userId) return;
    if (Platform.OS !== 'android' || !isNotificationBridgeAvailable()) return;
    let cancelled = false;
    checkNotificationListenerPermission().then((granted) => {
      if (cancelled || !shouldPromptCaptureConsent(record, granted)) return;
      promptedForUser.current = userId;
      router.push(CAPTURE_CONSENT_ROUTE as any);
    });
    return () => {
      cancelled = true;
    };
  }, [userId, needsPromptCheck, record]);
}
