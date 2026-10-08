// "Assinaturas e recorrências": as regras de apresentação da lista que o servidor calcula (GET /api/v1/ai/recurring).
// Lógica pura, coberta por testes em __tests__/recurring.test.ts; a tela e o cartão do Painel só desenham o que sai daqui.
import { spokenBRL } from '@/utils/a11y';
import type { RecurringItemResponse, RecurringListResponse, RecurringOverride } from '@/types/api';

export const RECURRING_QUERY_KEY = ['recurring'] as const;

export const RECURRING_TITLE = 'Assinaturas e recorrências';
export const RECURRING_EMPTY_TEXT = 'Ainda não há histórico suficiente: precisamos de 3 cobranças parecidas';
export const RECURRING_HINT = 'Toque em um item para ver as cobranças e corrigir o que estiver errado.';
export const RECURRING_TOTALS_NOTE =
  'Soma de assinaturas, contas fixas e parcelas. Pequenos gastos frequentes e itens ocultos não entram.';

const MONTHS = ['jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez'] as const;

export function formatBRL(amount: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(amount);
}

/** "2027-05" → "mai/2027". Um texto fora do formato volta como veio. */
export function monthLabel(month: string): string {
  const match = /^(\d{4})-(\d{2})$/.exec(month);
  if (!match) return month;
  const name = MONTHS[Number(match[2]) - 1];
  return name ? `${name}/${match[1]}` : month;
}

/** "2026-11-05" → "05/11/2026" (a data já vem no calendário de Brasília; nada de fuso aqui). */
export function dateLabel(date: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(date);
  return match ? `${match[3]}/${match[2]}/${match[1]}` : date;
}

/** O texto do cartão do Painel. */
export function recurringCardText(list: Pick<RecurringListResponse, 'monthlyTotal'>): string {
  return `Recorrências: ${formatBRL(list.monthlyTotal)}/mês`;
}

export function recurringCardLabel(list: Pick<RecurringListResponse, 'monthlyTotal'>): string {
  return `Recorrências: ${spokenBRL(list.monthlyTotal)} por mês. Abrir assinaturas e recorrências`;
}

export interface RecurringSection {
  readonly key: 'subscriptions' | 'fixedBills' | 'installments' | 'habits' | 'hidden';
  readonly title: string;
  readonly items: readonly RecurringItemResponse[];
}

/** As seções da tela, na ordem fixa, só as que têm item. */
export function recurringSections(list: RecurringListResponse): RecurringSection[] {
  const all: RecurringSection[] = [
    { key: 'subscriptions', title: 'Assinaturas', items: list.subscriptions ?? [] },
    { key: 'fixedBills', title: 'Contas fixas', items: list.fixedBills ?? [] },
    { key: 'installments', title: 'Parcelamentos', items: list.installments ?? [] },
    { key: 'habits', title: 'Pequenos gastos frequentes', items: list.habits ?? [] },
    { key: 'hidden', title: 'Ocultas', items: list.hidden ?? [] },
  ];
  return all.filter((section) => section.items.length > 0);
}

export function isRecurringEmpty(list: RecurringListResponse): boolean {
  return recurringSections(list).length === 0;
}

/** "R$ 39,90 por mês", "R$ 80,00 por semana", "R$ 1.200,00 por ano", "R$ 12,50 por compra". */
export function amountText(item: RecurringItemResponse): string {
  const amount = formatBRL(item.amount);
  if (item.cadence === 'Weekly') return `${amount} por semana`;
  if (item.cadence === 'Yearly') return `${amount} por ano`;
  if (item.cadence === 'Irregular') return `${amount} por compra`;
  return `${amount} por mês`;
}

export function spokenAmountText(item: RecurringItemResponse): string {
  const amount = spokenBRL(item.amount);
  if (item.cadence === 'Weekly') return `${amount} por semana`;
  if (item.cadence === 'Yearly') return `${amount} por ano`;
  if (item.cadence === 'Irregular') return `${amount} por compra`;
  return `${amount} por mês`;
}

/** As marcas de um item, na ordem em que aparecem. O texto é o que a pessoa lê. */
export function itemBadges(item: RecurringItemResponse): string[] {
  const badges: string[] = [];
  const flags = item.flags ?? [];
  if (item.override === 'NotRecurring') badges.push('marcada como não recorrente');
  if (item.override === 'Cancelled' && !flags.includes('ChargedAfterCancel')) badges.push('marcada como cancelada');
  if (flags.includes('ChargedAfterCancel')) badges.push('cobrou de novo depois de cancelada');
  if (flags.includes('Forgotten')) badges.push('talvez esquecida — vocês ainda usam?');
  if (flags.includes('PriceIncrease')) badges.push('ficou mais cara');
  if (flags.includes('New')) badges.push('nova');
  if (item.variableAmount) badges.push('valor variável');
  if (item.kind === 'Installment' && item.confidence === 'Low') badges.push('parcela provável');
  if (item.status === 'SuspectedDormant') badges.push('sem cobrança recente');
  if (item.status === 'Stopped') badges.push('parou de ser cobrada');
  return badges;
}

/** A linha de detalhe de um item: o que muda de um tipo para o outro. */
export function itemDetail(item: RecurringItemResponse): string {
  if (item.installment) {
    const { number, total, remainingAmount, endMonth } = item.installment;
    return `Parcela ${number} de ${total} · falta ${formatBRL(remainingAmount)} · termina em ${monthLabel(endMonth)}`;
  }

  if (item.cadence === 'Irregular') {
    return `${item.occurrences} compras em 30 dias · ${formatBRL(item.annualCost)} em 12 meses nesse ritmo`;
  }

  const parts = [`${formatBRL(item.annualCost)} por ano`];
  if (item.previousAmount !== null && item.previousAmount !== undefined) parts.push(`antes ${formatBRL(item.previousAmount)}`);
  if (item.variableAmount) parts.push(`última ${formatBRL(item.lastAmount)}`);
  if (item.nextExpected && item.status !== 'Stopped') parts.push(`próxima por volta de ${dateLabel(item.nextExpected)}`);
  if (item.person) parts.push(item.person.name);
  return parts.join(' · ');
}

/** O que o leitor de tela diz da linha (e o que os fluxos de tela procuram). */
export function itemLabel(item: RecurringItemResponse): string {
  const badges = itemBadges(item);
  const marks = badges.length > 0 ? ` ${badges.join(', ')}.` : '';
  return `${item.name}, ${spokenAmountText(item)}.${marks} Ver cobranças e opções`;
}

export interface RecurringAction {
  readonly override: RecurringOverride | null;
  readonly label: string;
}

const ACTION_LABEL: Record<RecurringOverride, string> = {
  NotRecurring: 'Não é recorrente',
  Cancelled: 'Cancelei',
  Subscription: 'É assinatura',
  FixedBill: 'É conta fixa',
};

/**
 * As correções que cabem num item. Com uma correção já feita, a primeira ação é desfazê-la. "Cancelei" só faz
 * sentido para assinatura e conta fixa; parcela não muda de tipo.
 */
export function itemActions(item: RecurringItemResponse): RecurringAction[] {
  const actions: RecurringAction[] = [];
  const current = item.override ?? null;
  if (current !== null) actions.push({ override: null, label: 'Desfazer a correção' });

  const wanted: RecurringOverride[] = ['NotRecurring'];
  if (item.kind === 'Subscription' || item.kind === 'FixedBill') wanted.push('Cancelled');
  if (item.kind !== 'Installment') {
    if (item.kind !== 'Subscription') wanted.push('Subscription');
    if (item.kind !== 'FixedBill') wanted.push('FixedBill');
  }

  for (const override of wanted) {
    if (override !== current) actions.push({ override, label: ACTION_LABEL[override] });
  }
  return actions;
}

/** O aviso depois de uma correção salva. */
export function overrideDoneMessage(name: string, override: RecurringOverride | null): string {
  switch (override) {
    case 'NotRecurring':
      return `${name} foi para Ocultas: não é recorrente.`;
    case 'Cancelled':
      return `${name} foi para Ocultas. Se cobrar de novo, ela volta com um aviso.`;
    case 'Subscription':
      return `${name} agora é assinatura.`;
    case 'FixedBill':
      return `${name} agora é conta fixa.`;
    default:
      return `Correção desfeita em ${name}.`;
  }
}
