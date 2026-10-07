// Consentimento do usuário neste aparelho: captura de notificações bancárias e uso do chat com IA (Google Gemini).
// Lógica pura (sem React Native), coberta por testes em __tests__/consent.test.ts.
//
// O consentimento pertence ao USUÁRIO neste aparelho: é guardado numa chave por id de usuário
// (consentStore.ts), então outro usuário que entre no mesmo celular é perguntado de novo.

/** Sobe quando o texto do consentimento muda de forma que exija nova resposta. */
export const CONSENT_VERSION = 1;

export interface CaptureConsent {
  /** Quando o usuário aceitou a captura (ISO UTC); null se nunca aceitou. */
  readonly acceptedAt: string | null;
  /** Quando o usuário respondeu pela última vez (aceitou, recusou ou desligou). null = nunca foi perguntado. */
  readonly decidedAt: string | null;
  /** Quando a tela de consentimento foi aberta sozinha pelo app (uma vez); depois disso só pelas Configurações. */
  readonly promptShownAt: string | null;
  /** Interruptor das configurações. Só vale ligado se houve aceite. */
  readonly enabled: boolean;
}

export interface AiChatConsent {
  readonly acceptedAt: string | null;
  readonly declinedAt: string | null;
}

/** Aceite do aviso de privacidade do Open Finance (wizard de conexão com o Meu Pluggy). */
export interface OpenFinanceConsent {
  readonly acceptedAt: string | null;
}

export interface ConsentRecord {
  readonly version: number;
  readonly capture: CaptureConsent;
  readonly aiChat: AiChatConsent;
  /** Ausente nos registros gravados antes do Open Finance existir: lido como "não aceito". */
  readonly openFinance: OpenFinanceConsent;
}

export const EMPTY_CONSENT: ConsentRecord = {
  version: CONSENT_VERSION,
  capture: { acceptedAt: null, decidedAt: null, promptShownAt: null, enabled: false },
  aiChat: { acceptedAt: null, declinedAt: null },
  openFinance: { acceptedAt: null },
};

function isoOrNull(value: unknown): string | null {
  return typeof value === 'string' && value && !Number.isNaN(Date.parse(value)) ? value : null;
}

/** Lê o registro guardado; qualquer coisa estranha (ou de outra versão) vira "nunca respondeu". */
export function parseConsent(raw: string | null | undefined): ConsentRecord {
  if (!raw) return EMPTY_CONSENT;
  try {
    const data = JSON.parse(raw);
    if (!data || typeof data !== 'object' || data.version !== CONSENT_VERSION) return EMPTY_CONSENT;
    const acceptedAt = isoOrNull(data.capture?.acceptedAt);
    return {
      version: CONSENT_VERSION,
      capture: {
        acceptedAt,
        decidedAt: isoOrNull(data.capture?.decidedAt),
        promptShownAt: isoOrNull(data.capture?.promptShownAt),
        // Ligado sem data de aceite não vale: nunca captura sem consentimento registrado.
        enabled: acceptedAt !== null && data.capture?.enabled === true,
      },
      aiChat: {
        acceptedAt: isoOrNull(data.aiChat?.acceptedAt),
        declinedAt: isoOrNull(data.aiChat?.declinedAt),
      },
      openFinance: { acceptedAt: isoOrNull(data.openFinance?.acceptedAt) },
    };
  } catch {
    return EMPTY_CONSENT;
  }
}

export function serializeConsent(record: ConsentRecord): string {
  return JSON.stringify(record);
}

/** A captura só roda com aceite registrado E o interruptor ligado. */
export function isCaptureAllowed(record: ConsentRecord): boolean {
  return record.capture.acceptedAt !== null && record.capture.enabled;
}

export function isAiChatAllowed(record: ConsentRecord): boolean {
  return record.aiChat.acceptedAt !== null;
}

/**
 * A tela de consentimento da captura aparece sozinha uma única vez, para quem já usa a captura (permissão de
 * leitura de notificações concedida) e ainda não respondeu. Quem nunca ligou a captura vê a tela só ao tentar ligar.
 */
export function shouldPromptCaptureConsent(record: ConsentRecord, listenerPermissionGranted: boolean): boolean {
  return listenerPermissionGranted && record.capture.decidedAt === null && record.capture.promptShownAt === null;
}

export function acceptCapture(record: ConsentRecord, nowIso: string): ConsentRecord {
  return { ...record, capture: { ...record.capture, acceptedAt: nowIso, decidedAt: nowIso, enabled: true } };
}

/** Recusa (ou desliga): mantém a data do aceite anterior, se houve, como histórico, mas desliga. */
export function declineCapture(record: ConsentRecord, nowIso: string): ConsentRecord {
  return { ...record, capture: { ...record.capture, decidedAt: nowIso, enabled: false } };
}

/** Religa pelo interruptor. Sem aceite anterior não liga: o chamador deve mostrar a tela de consentimento. */
export function setCaptureEnabled(record: ConsentRecord, enabled: boolean, nowIso: string): ConsentRecord {
  if (enabled && record.capture.acceptedAt === null) return record;
  return { ...record, capture: { ...record.capture, decidedAt: nowIso, enabled } };
}

/** A tela foi aberta sozinha: não abre de novo na próxima abertura do app, mesmo que o usuário saia sem responder. */
export function markCapturePromptShown(record: ConsentRecord, nowIso: string): ConsentRecord {
  return { ...record, capture: { ...record.capture, promptShownAt: nowIso } };
}

export function acceptAiChat(record: ConsentRecord, nowIso: string): ConsentRecord {
  return { ...record, aiChat: { acceptedAt: nowIso, declinedAt: null } };
}

export function declineAiChat(record: ConsentRecord, nowIso: string): ConsentRecord {
  return { ...record, aiChat: { acceptedAt: null, declinedAt: nowIso } };
}

export function isOpenFinanceAccepted(record: ConsentRecord): boolean {
  return record.openFinance.acceptedAt !== null;
}

export function acceptOpenFinance(record: ConsentRecord, nowIso: string): ConsentRecord {
  return { ...record, openFinance: { acceptedAt: nowIso } };
}

const MONTHS_PT_BR = [
  'janeiro', 'fevereiro', 'março', 'abril', 'maio', 'junho',
  'julho', 'agosto', 'setembro', 'outubro', 'novembro', 'dezembro',
] as const;

/** "6 de outubro de 2026" (data de Brasília). */
export function formatConsentDate(iso: string | null): string {
  if (!iso) return '';
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return '';
  // Brasília é UTC-3 o ano todo (sem horário de verão desde 2019).
  const d = new Date(ms - 3 * 60 * 60 * 1000);
  return `${d.getUTCDate()} de ${MONTHS_PT_BR[d.getUTCMonth()]} de ${d.getUTCFullYear()}`;
}
