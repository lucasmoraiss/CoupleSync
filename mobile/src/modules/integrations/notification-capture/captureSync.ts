// Decisão (pura) de como a captura de notificações acompanha o consentimento. O hook só executa o resultado.
//
// Regras que o teste fixa:
//  - o nativo nunca recebe "desligar" só porque o consentimento ainda não carregou (isso gravaria false no aparelho
//    para quem já aceitou e, se o processo morresse antes da leitura, a captura ficaria desligada);
//  - sem sessão (logout/expiração) o nativo termina desligado;
//  - a tela de consentimento abre sozinha no máximo uma vez.
import { isCaptureAllowed, shouldPromptCaptureConsent, type ConsentRecord } from '@/modules/privacy/consent';

export type NativeAction = 'enable' | 'disable' | 'leave';

export interface CaptureSyncInput {
  readonly sessionUserId: string | null;
  /** Usuário a quem o registro em memória pertence. */
  readonly consentUserId: string | null;
  readonly loaded: boolean;
  readonly record: ConsentRecord;
  /** Permissão "Acesso às notificações" do Android; null = ainda não consultada. */
  readonly listenerPermissionGranted: boolean | null;
}

export interface CaptureSyncDecision {
  readonly native: NativeAction;
  /** Escutar os eventos do módulo nativo e enviá-los. */
  readonly listen: boolean;
  /** Descartar eventos pendentes de envio. */
  readonly clearPending: boolean;
  /** Abrir a tela de consentimento agora. */
  readonly prompt: boolean;
}

export function decideCaptureSync(input: CaptureSyncInput): CaptureSyncDecision {
  if (!input.sessionUserId) {
    return { native: 'disable', listen: false, clearPending: true, prompt: false };
  }
  if (!input.loaded || input.consentUserId !== input.sessionUserId) {
    return { native: 'leave', listen: false, clearPending: false, prompt: false };
  }
  if (isCaptureAllowed(input.record)) {
    return { native: 'enable', listen: true, clearPending: false, prompt: false };
  }
  return {
    native: 'disable',
    listen: false,
    clearPending: true,
    prompt: shouldPromptCaptureConsent(input.record, input.listenerPermissionGranted === true),
  };
}
