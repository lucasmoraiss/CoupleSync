// "Assinaturas e recorrências": as regras de apresentação da lista que o servidor calcula (GET /api/v1/ai/recurring).
// Lógica pura, coberta por testes em __tests__/recurring.test.ts; a tela e o cartão do Painel só desenham o que sai daqui.
import { spokenBRL } from '@/utils/a11y';
import type { RecurringItemResponse, RecurringListResponse, RecurringOverride } from '@/types/api';

export const RECURRING_QUERY_KEY = ['recurring'] as const;

export const RECURRING_TITLE = 'Assinaturas e recorrências';
export const RECURRING_EMPTY_TEXT = 'Ainda não há histórico suficiente: precisamos de 3 cobranças parecidas';
export const RECURRING_HINT = 'Toque em um item para ver as cobranças e corrigir o que estiver errado.';
export const RECURRING_TOTALS_NOTE =
  'Soma de assinaturas, contas fixas e parcelas confirmadas. Cobrança anual entra no mês como 1/12; de um parcelamento, "Em 12 meses" é só o que falta pagar. Possíveis recorrências, pequenos gastos frequentes, parcelas prováveis e itens ocultos não entram.';
/** O texto da seção "Possíveis recorrências": o que é, que não soma, e o que fazer. */
export const RECURRING_POSSIBLE_NOTE =
  'Parece se repetir, mas ainda não temos certeza. Não entra no total. Toque em um item e diga se é recorrente.';
/** A pergunta acima das duas ações de um item possível. */
export const RECURRING_POSSIBLE_QUESTION = 'É recorrente?';
/** Só há possíveis: o total zerado não é defeito, é falta de confirmação. */
export const RECURRING_ONLY_POSSIBLE_TEXT =
  'Nada confirmado ainda. O que parece se repetir está em "Possíveis recorrências", logo abaixo: diga se é recorrente para entrar no total.';
/** O que nunca vira item (regra de privacidade do servidor): a tela diz, para o vazio não parecer defeito. */
export const RECURRING_NOT_LISTED_NOTE = 'Pix e transferências para pessoas não aparecem aqui, mesmo que se repitam.';
/** A lista na tela é antiga: a última consulta falhou. */
export const RECURRING_STALE_TEXT = 'Não foi possível atualizar agora. Esta é a última lista carregada.';

/** O nome da entrada fixa da tela (Configurações). */
export const RECURRING_ENTRY_LABEL = 'Assinaturas e contas fixas';
/** O cartão do Painel sem nenhuma recorrência: diz o que vai aparecer ali. */
export const RECURRING_CARD_EMPTY_TEXT = 'Assinaturas e contas fixas aparecem aqui depois de 3 cobranças parecidas';
export const RECURRING_CARD_EMPTY_LABEL =
  'Assinaturas e contas fixas: ainda não há histórico suficiente. Elas aparecem aqui depois de 3 cobranças parecidas. Abrir assinaturas e recorrências';
/** O cartão do Painel quando a consulta falhou e não há lista para mostrar. */
export const RECURRING_CARD_ERROR_TEXT = 'Não foi possível carregar as recorrências';
export const RECURRING_CARD_ERROR_LABEL = 'Não foi possível carregar as recorrências. Tentar de novo';

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

type CardList = Pick<RecurringListResponse, 'monthlyTotal' | 'possible'>;

/** O texto do cartão do Painel: o total é só do que está confirmado; as possíveis são contadas ao lado. */
export function recurringCardText(list: CardList): string {
  const possible = list.possible?.length ?? 0;
  const more = possible === 0 ? '' : ` · +${possible} ${possible === 1 ? 'possível' : 'possíveis'}`;
  return `Recorrências: ${formatBRL(list.monthlyTotal)}/mês${more}`;
}

export function recurringCardLabel(list: CardList): string {
  const possible = list.possible?.length ?? 0;
  const more =
    possible === 0 ? '.' : `, mais ${possible} ${possible === 1 ? 'possível recorrência' : 'possíveis recorrências'} para conferir.`;
  return `Recorrências: ${spokenBRL(list.monthlyTotal)} por mês${more} Abrir assinaturas e recorrências`;
}

/**
 * Possível recorrência: o servidor achou um padrão sem prova (confiança baixa), não é parcela, e a pessoa ainda não
 * disse que é recorrente. Fica na seção própria e fora do total.
 */
export function isPossibleItem(item: RecurringItemResponse): boolean {
  return item.confidence === 'Low' && item.kind !== 'Installment' && item.override !== 'Subscription' && item.override !== 'FixedBill';
}

/** Há possíveis e nada confirmado (nem assinatura, nem conta fixa, nem parcelamento): o total zerado pede explicação. */
export function hasOnlyPossible(list: RecurringListResponse): boolean {
  const confirmed = (list.subscriptions?.length ?? 0) + (list.fixedBills?.length ?? 0) + (list.installments?.length ?? 0);
  return confirmed === 0 && (list.possible?.length ?? 0) > 0;
}

export interface RecurringSection {
  readonly key: 'subscriptions' | 'fixedBills' | 'installments' | 'possible' | 'habits' | 'hidden';
  readonly title: string;
  readonly items: readonly RecurringItemResponse[];
}

/** As seções da tela, na ordem fixa, só as que têm item. */
export function recurringSections(list: RecurringListResponse): RecurringSection[] {
  const all: RecurringSection[] = [
    { key: 'subscriptions', title: 'Assinaturas', items: list.subscriptions ?? [] },
    { key: 'fixedBills', title: 'Contas fixas', items: list.fixedBills ?? [] },
    { key: 'installments', title: 'Parcelamentos', items: list.installments ?? [] },
    // Logo abaixo do que está confirmado (e soma no total): o que só parece se repetir.
    { key: 'possible', title: 'Possíveis recorrências', items: list.possible ?? [] },
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
  // De uma possível recorrência ainda não se diz "esquecida", "ficou mais cara" nem "nova".
  const flags = isPossibleItem(item) ? [] : (item.flags ?? []);
  if (item.override === 'NotRecurring') badges.push('marcada como não recorrente');
  if (item.override === 'Cancelled' && !flags.includes('ChargedAfterCancel')) badges.push('marcada como cancelada');
  if (flags.includes('ChargedAfterCancel')) badges.push('cobrou de novo depois de cancelada');
  if (flags.includes('Forgotten')) badges.push('talvez esquecida — vocês ainda usam?');
  if (flags.includes('PriceIncrease')) badges.push('ficou mais cara');
  if (flags.includes('New')) badges.push('nova');
  if (item.variableAmount) badges.push('valor variável');
  if (item.kind === 'Installment' && item.confidence === 'Low') badges.push('parcela provável');
  if (item.status === 'SuspectedDormant') badges.push('sem cobrança recente');
  // Cancelada que voltou a cobrar: a série antiga continua "parada", mas dizer isso junto contradiz o aviso.
  if (item.status === 'Stopped' && !flags.includes('ChargedAfterCancel')) badges.push('parou de ser cobrada');
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

  // O item anual já diz "R$ X por ano" ao lado: repetir aqui não acrescenta nada.
  const parts = item.cadence === 'Yearly' ? [] : [`${formatBRL(item.annualCost)} por ano`];
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
  if (isPossibleItem(item)) {
    return `${item.name}, ${spokenAmountText(item)}.${marks} Possível recorrência, fora do total. Ver cobranças e dizer se é recorrente`;
  }
  return `${item.name}, ${spokenAmountText(item)}.${marks} Ver cobranças e opções`;
}

export interface RecurringAction {
  readonly override: RecurringOverride | null;
  readonly label: string;
  /** O que o leitor de tela diz, quando o texto do botão sozinho é curto demais ("Não é"). */
  readonly spoken?: string;
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
 * Possível recorrência: só as duas respostas à pergunta "É recorrente?". O "sim" guarda o tipo que o servidor achou
 * (assinatura ou conta fixa): o item passa para essa lista e entra no total.
 */
export function itemActions(item: RecurringItemResponse): RecurringAction[] {
  if (isPossibleItem(item)) {
    return [
      { override: item.kind === 'Subscription' ? 'Subscription' : 'FixedBill', label: 'É recorrente', spoken: 'Sim, é recorrente' },
      { override: 'NotRecurring', label: 'Não é', spoken: 'Não, não é recorrente' },
    ];
  }

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

/** O aviso depois de uma correção salva. `wasPossible`: o item era uma possível recorrência (estava fora do total). */
export function overrideDoneMessage(name: string, override: RecurringOverride | null, wasPossible = false): string {
  const counts = wasPossible ? ' e entra no total' : '';
  switch (override) {
    case 'NotRecurring':
      return `${name} foi para Ocultas: não é recorrente.`;
    case 'Cancelled':
      return `${name} foi para Ocultas. Se cobrar de novo, ela volta com um aviso.`;
    case 'Subscription':
      return `${name} agora é assinatura${counts}.`;
    case 'FixedBill':
      return `${name} agora é conta fixa${counts}.`;
    default:
      return `Correção desfeita em ${name}.`;
  }
}
