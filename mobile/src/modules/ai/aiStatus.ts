// Análise com IA: o que as telas decidem a partir de GET /api/v1/ai/status. Lógica pura (sem React Native),
// coberta por __tests__/aiStatus.test.ts.
//
// A IA vem ligada para todo grupo: não há pergunta nem aceite. Quem decide se a IA existe (`available`) e se o
// grupo a desligou de propósito (`enabled: false`) é o servidor. Os campos de aceite e de pergunta do status
// (`acceptedBy`, `myAcceptance`, `onboardingPending`) continuam vindo, mas nenhuma tela decide nada por eles. O
// aceite antigo guardado no aparelho (campo `aiChat` do registro de consentimento) também não é consultado.
import { getApiErrorCode, getApiErrorMessage, getApiErrorStatus } from '@/services/apiError';
import type { AiStatusResponse, AiUsageDayResponse, AiUsageProviderResponse } from '@/types/api';

/**
 * Versão do texto "Análise com IA" que este app mostra, a mesma do servidor (AiConsent.CurrentVersion). Vai no
 * pedido de ligar de novo (POST /ai/consent), que o servidor aceita em qualquer versão; a API anterior exigia esta.
 */
export const AI_CONSENT_VERSION = 2;

/**
 * Os destinos que o texto da análise com IA deve citar: os que o servidor diz ter ligados AGORA. Sem status, ou com
 * um status guardado de antes da versão atual do texto (que não conhecia o destino novo), devolve null — e o texto
 * cita todos os que esta versão cobre (privacyContent). Ninguém lê menos do que pode acontecer.
 */
export function aiDestinations(status: AiStatusResponse | null): AiStatusResponse['providers'] | null {
  if (!status || status.consentVersion !== AI_CONSENT_VERSION || status.providers.length === 0) return null;
  return status.providers;
}

/** O botão "Assistente" (Painel) só existe quando o servidor diz que há IA. Sem status: não aparece. */
export function isAssistantVisible(status: AiStatusResponse | null): boolean {
  return status?.available === true;
}

/**
 * `off-for-group`: há IA no servidor e alguém do grupo a desligou de propósito (ou, numa API anterior a esta
 * entrega, o grupo ainda não tinha aceite: para a tela é o mesmo, e ligar de novo resolve os dois).
 */
export type AssistantGate = 'loading' | 'error' | 'unavailable' | 'off-for-group' | 'ready';

/**
 * O que a tela do Assistente mostra: a conversa, direto, sempre que há IA e o grupo não a desligou. Com um status
 * já conhecido, uma falha ao atualizar não derruba a tela.
 */
export function assistantGate(status: AiStatusResponse | null, loadFailed: boolean): AssistantGate {
  if (!status) return loadFailed ? 'error' : 'loading';
  if (!status.available) return 'unavailable';
  return status.enabled ? 'ready' : 'off-for-group';
}

export type AiGroupState = 'unavailable' | 'off-for-group' | 'on';

/** O estado que Configurações > Inteligência artificial mostra. Não depende de aceite de ninguém. */
export function aiGroupState(status: AiStatusResponse): AiGroupState {
  if (!status.available) return 'unavailable';
  return status.enabled ? 'on' : 'off-for-group';
}

export const AI_GROUP_STATE_TEXT: Readonly<Record<AiGroupState, string>> = {
  on: 'Ligada',
  'off-for-group': 'Desligada para o grupo',
  unavailable: 'Indisponível no momento',
};

// Frases por desfecho (o que o Assistente mostra). Fixas no app para não depender do texto do servidor.
const CHAT_ERROR_MESSAGES: Readonly<Record<string, string>> = {
  AI_DAILY_BUDGET_EXHAUSTED: 'A cota de IA do grupo para hoje acabou. Volta à meia-noite.',
  AI_GLOBAL_BUDGET_EXHAUSTED: 'A IA do app atingiu o limite de uso de hoje. Volta à meia-noite. Os números do app continuam atualizados.',
  AI_PROVIDER_FAILED: 'A IA não respondeu agora. Tente de novo em alguns minutos.',
  AI_CHAT_DISABLED: 'A análise com IA não está disponível no momento.',
  // O servidor só responde isto para grupo que desligou a IA (a API anterior também respondia para grupo sem aceite).
  AI_CONSENT_REQUIRED: 'A IA está desligada para este grupo. Para voltar a usar, ligue em Configurações > Inteligência artificial.',
};

export const CHAT_SEND_FALLBACK_MESSAGE = 'Erro ao enviar mensagem. Tente novamente.';

/** Texto para o aviso de erro do Assistente: cota, IA fora do ar, sem internet, tempo esgotado. */
export function chatErrorMessage(error: unknown): string {
  const code = getApiErrorStatus(error) === undefined ? undefined : getApiErrorCode(error);
  if (code && CHAT_ERROR_MESSAGES[code]) return CHAT_ERROR_MESSAGES[code];
  return getApiErrorMessage(error, CHAT_SEND_FALLBACK_MESSAGE);
}

/** O servidor disse que a IA foi desligada (para todos ou para o grupo) no meio da conversa: o status é consultado de novo. */
export function chatErrorChangesStatus(error: unknown): boolean {
  const code = getApiErrorCode(error);
  return code === 'AI_CONSENT_REQUIRED' || code === 'AI_CHAT_DISABLED';
}

/** Uma linha por modelo no consumo: chamadas de hoje e a parte da cota diária, quando ela é conhecida. */
export function modelQuotaText(provider: AiUsageProviderResponse): string {
  const calls = `${provider.calls} ${provider.calls === 1 ? 'chamada' : 'chamadas'} hoje`;
  const quota =
    provider.limit === null || provider.percentUsed === null
      ? 'cota ainda não conhecida'
      : `${provider.percentUsed}% da cota diária (${provider.limit})`;
  return [calls, quota, ...(provider.exhaustedToday ? ['esgotado hoje'] : [])].join(' · ');
}

export function groupBudgetText(budget: { readonly callsToday: number; readonly callLimit: number }): string {
  // Chamadas, não perguntas: uma pergunta que passa para o segundo modelo conta duas.
  return `${budget.callsToday} de ${budget.callLimit} chamadas à IA do grupo hoje`;
}

export interface UsageTotal {
  readonly calls: number;
  readonly tokens: number;
  readonly failures: number;
}

/** Hoje (o último dia da lista, que vem em ordem) e a soma do período pedido. */
export function usageTotals(days: readonly AiUsageDayResponse[]): { readonly today: UsageTotal; readonly period: UsageTotal } {
  const total = (list: readonly AiUsageDayResponse[]): UsageTotal => ({
    calls: list.reduce((sum, day) => sum + day.calls, 0),
    tokens: list.reduce((sum, day) => sum + day.inputTokens + day.outputTokens, 0),
    failures: list.reduce((sum, day) => sum + day.failures, 0),
  });
  return { today: total(days.slice(-1)), period: total(days) };
}

/**
 * O campo de IA que vai junto com o extrato importado e com a sincronização do Open Finance: ligado sempre que a IA
 * existe e o grupo não a desligou (o servidor confere de novo ao processar).
 */
export function aiUploadConsent(status: AiStatusResponse | null): boolean {
  return status?.available === true && status.enabled;
}

// ─── O status enquanto o servidor não respondeu (Painel) ─────────────────────────────────────────────────────

/**
 * O que o Painel mostra sobre a IA quando ainda não há status nenhum (nem o guardado no aparelho): a pessoa nunca
 * fica com um Painel "sem IA" sem saber por quê.
 * - `checking`: a consulta está em andamento;
 * - `retrying`: a consulta falhou e outra tentativa já está marcada;
 * - `failed`: as tentativas acabaram; só volta a tentar com o botão, ao puxar para atualizar ou num novo foco.
 */
export type AiStatusNotice = 'none' | 'checking' | 'retrying' | 'failed';

export function aiStatusNotice(status: AiStatusResponse | null, loadFailed: boolean, retrying: boolean): AiStatusNotice {
  if (status) return 'none';
  if (!loadFailed) return 'checking';
  return retrying ? 'retrying' : 'failed';
}

export const AI_STATUS_NOTICE_TEXT: Readonly<Record<Exclude<AiStatusNotice, 'none'>, string>> = {
  checking: 'Verificando a análise com IA…',
  retrying: 'Não foi possível verificar a análise com IA. Tentando de novo…',
  failed: 'Não foi possível verificar a análise com IA. Verifique a internet.',
};

/** O aviso como o Painel o desenha: o texto, o ícone e, depois de uma falha, o botão "Verificar agora". */
export interface AiStatusNoticeView {
  readonly text: string;
  readonly icon: 'sparkles-outline' | 'cloud-offline-outline';
  /** O toque em "Verificar agora": consulta o servidor na hora. Null enquanto a consulta está em andamento (sem botão). */
  readonly checkNow: (() => void) | null;
}

/** Null: há status (do servidor ou guardado no aparelho) e o Painel não mostra aviso nenhum. */
export function aiStatusNoticeView(
  status: AiStatusResponse | null,
  loadFailed: boolean,
  retrying: boolean,
  refresh: () => unknown,
): AiStatusNoticeView | null {
  const notice = aiStatusNotice(status, loadFailed, retrying);
  if (notice === 'none') return null;
  const checking = notice === 'checking';
  return {
    text: AI_STATUS_NOTICE_TEXT[notice],
    icon: checking ? 'sparkles-outline' : 'cloud-offline-outline',
    checkNow: checking ? null : () => void refresh(),
  };
}

/** Esperas entre as novas tentativas da consulta do status que falhou (cobrem a API acordando: cerca de 2 minutos). */
export const AI_STATUS_RETRY_DELAYS_MS: readonly number[] = [3_000, 8_000, 20_000, 40_000, 60_000];

// ─── O último status, guardado no aparelho por pessoa ────────────────────────────────────────────────────────

const STORED_STATUS_VERSION = 1;

export function serializeStoredAiStatus(coupleId: string, status: AiStatusResponse): string {
  return JSON.stringify({ v: STORED_STATUS_VERSION, coupleId, status });
}

/**
 * Lê o que foi guardado. Só vale se for deste grupo e tiver a forma que as telas usam; qualquer outra coisa
 * (outro grupo, versão antiga, texto corrompido) é como não ter nada guardado.
 */
export function parseStoredAiStatus(raw: string | null | undefined, coupleId: string): AiStatusResponse | null {
  if (!raw) return null;
  try {
    const data = JSON.parse(raw) as { v?: unknown; coupleId?: unknown; status?: Partial<AiStatusResponse> | null };
    const status = data?.status;
    if (data?.v !== STORED_STATUS_VERSION || data.coupleId !== coupleId || !status || typeof status !== 'object') return null;
    const wellFormed =
      typeof status.available === 'boolean' &&
      typeof status.enabled === 'boolean' &&
      typeof status.onboardingPending === 'boolean' &&
      Array.isArray(status.acceptedBy) &&
      status.acceptedBy.every((a) => a && typeof a.userId === 'string' && typeof a.name === 'string') &&
      Array.isArray(status.providers) &&
      typeof status.features === 'object' && status.features !== null &&
      typeof status.budget === 'object' && status.budget !== null &&
      (status.myAcceptance === null || (typeof status.myAcceptance === 'object' && status.myAcceptance !== undefined));
    return wellFormed ? (status as AiStatusResponse) : null;
  } catch {
    return null;
  }
}

// ─── Configurações > Inteligência artificial ─────────────────────────────────────────────────────────────────

export interface AiSettingsActions {
  /** "Ligar de novo": direto, sem tela de pergunta. Só quando há IA no servidor e o grupo a desligou. */
  readonly turnOn: boolean;
  /** "Desligar para o grupo": qualquer membro pode, com a IA ligada. */
  readonly turnOffForGroup: boolean;
}

export function aiSettingsActions(status: AiStatusResponse): AiSettingsActions {
  return {
    turnOn: status.available && !status.enabled,
    turnOffForGroup: status.enabled,
  };
}
