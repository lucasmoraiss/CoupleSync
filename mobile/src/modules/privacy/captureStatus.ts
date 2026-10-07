// O que as telas dizem sobre a captura de notificações. Ela só funciona de verdade com TRÊS coisas: o aceite do
// usuário, o interruptor ligado e a permissão "Acesso às notificações" do Android. Puro, coberto por teste.
import { formatConsentDate, type CaptureConsent } from './consent';

/** O que falta para a captura funcionar; null = nada (ou não dá para saber da permissão). */
export type CaptureMissing = 'consent' | 'switch' | 'permission' | null;

export interface CaptureStatus {
  /** Posição do interruptor: ligado só quando a captura está de fato funcionando (ou nada indica o contrário). */
  readonly switchOn: boolean;
  /** Texto sob o interruptor nas Configurações. */
  readonly description: string;
  readonly missing: CaptureMissing;
}

/**
 * `permissionGranted`: true/false conforme o Android; null quando ainda não foi consultada ou não se aplica
 * (aparelho sem o módulo nativo). Com null não se afirma que falta permissão.
 */
export function describeCaptureStatus(
  capture: CaptureConsent,
  permissionGranted: boolean | null,
  formatDate: (iso: string) => string = formatConsentDate,
): CaptureStatus {
  if (capture.acceptedAt === null) {
    return {
      switchOn: false,
      description: 'Desligada. Ao ligar, você lê o que é lido e enviado e decide.',
      missing: 'consent',
    };
  }

  const accepted = `Aceita em ${formatDate(capture.acceptedAt)}.`;
  if (!capture.enabled) {
    return { switchOn: false, description: `${accepted} Desligada: nada é enviado.`, missing: 'switch' };
  }
  if (permissionGranted === false) {
    return {
      switchOn: false,
      description: `${accepted} Ainda não funciona: falta permitir o "Acesso às notificações" do CoupleSync no Android. Toque no interruptor para abrir essa tela do sistema.`,
      missing: 'permission',
    };
  }
  return { switchOn: true, description: `${accepted} Ligada.`, missing: null };
}

/** Aviso da tela de Transações quando a permissão do Android não está dada; null = sem aviso. */
export function captureBannerText(capture: CaptureConsent, permissionGranted: boolean | null): string | null {
  if (permissionGranted !== false) return null;
  return describeCaptureStatus(capture, permissionGranted).missing === 'permission'
    ? 'Captura ligada, mas falta a permissão do Android'
    : 'Captura de notificações desativada';
}

export type CaptureToggleAction =
  /** Nunca aceitou: abrir a tela de consentimento. */
  | 'open-consent'
  /** Já aceitou e ligou, só falta a permissão: abrir a tela do Android. */
  | 'open-system-settings'
  /** Gravar o interruptor ligado e, faltando a permissão, abrir a tela do Android em seguida. */
  | 'enable-then-system-settings'
  | 'enable'
  | 'disable';

/** O que o toque no interruptor faz, dado o estado atual. */
export function captureToggleAction(
  wantOn: boolean,
  capture: CaptureConsent,
  permissionGranted: boolean | null,
): CaptureToggleAction {
  if (!wantOn) return 'disable';
  if (capture.acceptedAt === null) return 'open-consent';
  const permissionMissing = permissionGranted === false;
  if (capture.enabled) return permissionMissing ? 'open-system-settings' : 'enable';
  return permissionMissing ? 'enable-then-system-settings' : 'enable';
}
