// Decisão (pura) de como a captura de notificações acompanha o consentimento. O hook só executa o resultado.
//
// Regras que o teste fixa:
//  - o nativo nunca recebe "desligar" só porque o consentimento ainda não carregou (isso gravaria false no aparelho
//    para quem já aceitou e, se o processo morresse antes da leitura, a captura ficaria desligada);
//  - sem sessão (logout/expiração) o nativo termina desligado;
//  - a tela de consentimento abre sozinha no máximo uma vez;
//  - ao ligar, a escuta do JS é registrada ANTES de o nativo ser avisado (é o aviso que faz o nativo entregar o que
//    guardou enquanto ninguém escutava); soltar a escuta nunca desliga o nativo.
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

/** O que a decisão manda fazer, nas mãos de quem chama (o hook passa as funções reais; o teste, registradores). */
export interface CaptureEffects {
  /** Passa a escutar os eventos do módulo nativo; devolve como parar de escutar. */
  attachListener(): () => void;
  /** Avisa o serviço nativo. `true` também faz o nativo entregar os eventos que guardou sem ouvinte. */
  setNativeEnabled(enabled: boolean): void;
  clearPending(): void;
}

/**
 * Executa a decisão na ordem que importa. Devolve a limpeza (parar de escutar) quando passou a escutar.
 * A limpeza NÃO desliga o nativo: ela roda sempre que a área principal sai de cena (ir criar outro grupo, o
 * Android destruir a tela), e desligar ali gravaria "captura desligada" no aparelho de quem não pediu isso.
 * Quem desliga é a decisão `disable` (consentimento retirado, sem sessão) e a saída da conta.
 */
export function applyCaptureDecision(decision: CaptureSyncDecision, effects: CaptureEffects): (() => void) | undefined {
  if (decision.native === 'disable') effects.setNativeEnabled(false);
  if (decision.clearPending) effects.clearPending();
  if (!decision.listen) return undefined;

  const detach = effects.attachListener();
  if (decision.native === 'enable') effects.setNativeEnabled(true);
  return detach;
}
