// Análise com IA: o que as telas decidem a partir de GET /api/v1/ai/status. Lógica pura (sem React Native),
// coberta por __tests__/aiStatus.test.ts.
//
// Quem decide se a IA existe (`available`) e se o grupo a ativou (`enabled`) é o servidor: um aceite vale para o
// grupo inteiro e fica registrado lá. O aceite antigo guardado no aparelho (campo `aiChat` do registro de
// consentimento) não é mais consultado por ninguém.
import { getApiErrorCode, getApiErrorMessage, getApiErrorStatus } from '@/services/apiError';
import type { AiStatusResponse, AiUsageDayResponse, AiUsageProviderResponse } from '@/types/api';

/** Versão do texto "Análise com IA" que este app mostra. A mesma do servidor (AiConsent.CurrentVersion). */
export const AI_CONSENT_VERSION = 1;

/** O botão "Assistente" (Painel) só existe quando o servidor diz que há IA. Sem status: não aparece. */
export function isAssistantVisible(status: AiStatusResponse | null): boolean {
  return status?.available === true;
}

/** Cartão "Análise com IA desligada — Ativar" do Painel: há IA no servidor e o grupo ainda não ativou. */
export function shouldShowActivationCard(status: AiStatusResponse | null): boolean {
  return status?.available === true && !status.enabled;
}

export type AssistantGate = 'loading' | 'error' | 'unavailable' | 'needs-activation' | 'ready';

/** O que a tela do Assistente mostra. Com um status já conhecido, uma falha ao atualizar não derruba a tela. */
export function assistantGate(status: AiStatusResponse | null, loadFailed: boolean): AssistantGate {
  if (!status) return loadFailed ? 'error' : 'loading';
  if (!status.available) return 'unavailable';
  return status.enabled ? 'ready' : 'needs-activation';
}

/**
 * A tela de boas-vindas abre sozinha quando o servidor diz que a pergunta é devida a esta pessoa neste grupo —
 * e só uma vez por abertura do app: quem volta sem responder não é levado a ela de novo a cada foco do Painel.
 */
export function shouldOpenWelcome(status: AiStatusResponse | null, alreadyShown: boolean): boolean {
  return status?.available === true && status.onboardingPending && !alreadyShown;
}

export type WelcomeView =
  | { readonly kind: 'unavailable' }
  /** A própria pessoa já ativou: nada a perguntar. */
  | { readonly kind: 'done' }
  | { readonly kind: 'ask' }
  /** Outra pessoa do grupo já ativou: aviso, com "Entendi" e "Desligar para o grupo". */
  | { readonly kind: 'activated-by-other'; readonly names: string };

function firstName(name: string): string {
  return name.trim().split(/\s+/)[0] ?? name;
}

function joinInPortuguese(items: readonly string[]): string {
  if (items.length <= 1) return items.join('');
  return `${items.slice(0, -1).join(', ')} e ${items[items.length - 1]}`;
}

export function welcomeView(status: AiStatusResponse, myUserId: string | null): WelcomeView {
  if (!status.available) return { kind: 'unavailable' };
  if (status.myAcceptance) return { kind: 'done' };
  const others = status.acceptedBy.filter((acceptance) => acceptance.userId !== myUserId);
  if (status.enabled && others.length > 0) {
    return { kind: 'activated-by-other', names: joinInPortuguese(others.map((acceptance) => firstName(acceptance.name))) };
  }
  return { kind: 'ask' };
}

/** "Ativada por Ana em 8 de outubro de 2026" / "Desligada" (Configurações > Inteligência artificial). */
export function activationSummary(status: AiStatusResponse, formatDate: (iso: string | null) => string): string {
  if (!status.available) return 'Indisponível no momento';
  if (!status.enabled || status.acceptedBy.length === 0) return 'Desligada';
  return `Ativada ${status.acceptedBy.map((acceptance) => `por ${acceptance.name} em ${formatDate(acceptance.acceptedAtUtc)}`).join(' e ')}`;
}

// Frases por desfecho (o que o Assistente mostra). Fixas no app para não depender do texto do servidor.
const CHAT_ERROR_MESSAGES: Readonly<Record<string, string>> = {
  AI_DAILY_BUDGET_EXHAUSTED: 'A cota de IA do grupo para hoje acabou. Volta à meia-noite.',
  AI_GLOBAL_BUDGET_EXHAUSTED: 'A IA do app atingiu o limite de uso de hoje. Volta à meia-noite. Os números do app continuam atualizados.',
  AI_PROVIDER_FAILED: 'A IA não respondeu agora. Tente de novo em alguns minutos.',
  AI_CHAT_DISABLED: 'A análise com IA não está disponível no momento.',
  AI_CONSENT_REQUIRED: 'A análise com IA está desligada para este grupo. Ative em Configurações > Inteligência artificial.',
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
 * O campo de IA que vai junto com o extrato importado: as descrições só podem ir ao classificador quando a IA
 * existe e o grupo a ativou no servidor (que confere de novo ao processar).
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
  /** "Ativar para o grupo" (leva ao texto e à pergunta). */
  readonly activate: boolean;
  /** "Desligar para o grupo": qualquer membro pode, com a análise ativada. */
  readonly turnOffForGroup: boolean;
  /** "Retirar meu aceite": só quem aceitou. */
  readonly withdrawMine: boolean;
}

export function aiSettingsActions(status: AiStatusResponse): AiSettingsActions {
  return {
    activate: status.available && !status.enabled,
    turnOffForGroup: status.enabled,
    withdrawMine: status.enabled && status.myAcceptance !== null,
  };
}
