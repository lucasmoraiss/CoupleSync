// A tela de consentimento da captura (useCaptureConsentSync, para quem já deu o "Acesso às notificações" no Android
// e nunca respondeu) abre sozinha na chegada à área principal. Aqui ficam a marca de que ela está abrindo e a
// pergunta "ela vai abrir?", feita por outra tela que também abra sozinha, para esperar a vez dela. Quem perguntava
// era a pergunta da análise com IA, que não existe mais (a IA vem ligada para todo grupo): hoje nenhuma tela
// consulta `isCapturePromptAhead`.
//
// Lógica pura (quem chama passa as leituras reais), coberta por __tests__/capturePromptGate.test.ts.
import type { ConsentRecord } from '@/modules/privacy/consent';
import { decideCaptureSync } from './captureSync';

/** Para quem a tela de consentimento da captura começou a abrir nesta abertura do app. */
let openingForUserId: string | null = null;

/**
 * Chamado por useCaptureConsentSync no instante em que decide abrir a tela — antes de gravar "já mostrei" e de
 * navegar. Entre uma coisa e outra o registro deixa de dizer "pendente"; esta marca cobre esse intervalo.
 */
export function noteCapturePromptOpening(userId: string | null): void {
  openingForUserId = userId;
}

export function clearCapturePromptOpening(): void {
  openingForUserId = null;
}

/**
 * Para o `useFocusEffect` da tela de consentimento da captura: quando ela perde o foco, a marca é desfeita. Quem
 * sai pelo voltar do Android sem responder não deixa a marca de pé — outra tela que espere por esta abre na volta
 * ao Painel, sem ter de reabrir o app. (Com a tela da captura em foco a marca não faz falta: o Painel, fora de foco, não
 * abre tela nenhuma; e a tela da captura não abre sozinha uma segunda vez.)
 */
export function captureConsentScreenFocusEffect(): () => void {
  return clearCapturePromptOpening;
}

export interface CapturePromptProbe {
  readonly sessionUserId: string | null;
  /** O leitor nativo de notificações existe neste aparelho (Android, APK com o módulo). */
  readonly bridgeAvailable: boolean;
  readConsent(): { readonly userId: string | null; readonly loaded: boolean; readonly record: ConsentRecord };
  /** Lê o registro de consentimento do usuário (não faz nada se já estiver lido). */
  loadConsent(userId: string): Promise<void>;
  /** Permissão "Acesso às notificações" do Android. */
  checkPermission(): Promise<boolean>;
}

/**
 * A tela de consentimento da captura vai abrir sozinha (ou está abrindo) para a pessoa logada? Na dúvida
 * (registro que não pôde ser lido, permissão que não pôde ser consultada) a resposta é não: quem espera por esta
 * tela não fica preso por um defeito de outra parte do app.
 */
export async function isCapturePromptAhead(probe: CapturePromptProbe): Promise<boolean> {
  const userId = probe.sessionUserId;
  if (!userId || !probe.bridgeAvailable) return false;

  let consent = probe.readConsent();
  if (!consent.loaded || consent.userId !== userId) {
    try {
      await probe.loadConsent(userId);
    } catch {
      return false;
    }
    consent = probe.readConsent();
  }
  if (!consent.loaded || consent.userId !== userId) return false;

  // Já respondeu (aceitou ou recusou): a tela da captura não abre mais sozinha.
  if (consent.record.capture.decidedAt !== null) return false;
  if (openingForUserId === userId) return true;

  let granted = false;
  try {
    granted = await probe.checkPermission();
  } catch {
    return false;
  }
  return decideCaptureSync({
    sessionUserId: userId,
    consentUserId: consent.userId,
    loaded: true,
    record: consent.record,
    listenerPermissionGranted: granted,
  }).prompt;
}
