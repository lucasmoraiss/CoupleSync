// Open Finance (Meu Pluggy): regras da tela "Revisão do banco". Lógica pura (sem React Native), coberta por
// __tests__/review.test.ts.
//
// Nada do banco vira transação sem alguém do grupo confirmar. O valor nunca é editável: o app manda só o id da
// linha e, quando a pessoa trocou, a categoria.
import { monthLabelFromIso } from '@/utils/format';
import type { BankReviewLineResponse, BankReviewMonthResponse, ConfirmBankReviewRequest } from '@/types/api';

/** Linhas por chamada de confirmação. O servidor aceita até 500. */
export const CONFIRM_BATCH_SIZE = 200;

/** Lançamento que o banco ainda não efetivou: aparece em cinza, sem caixa, e o servidor recusa confirmá-lo. */
export function isSelectable(line: Pick<BankReviewLineResponse, 'bankStatus'>): boolean {
  return line.bankStatus !== 'Pending';
}

/** "Selecionar tudo": todas as despesas da lista, menos as pendentes no banco. */
export function selectAllIds(lines: readonly BankReviewLineResponse[]): string[] {
  return lines.filter(isSelectable).map((line) => line.id);
}

export function allSelected(lines: readonly BankReviewLineResponse[], selected: ReadonlySet<string>): boolean {
  const ids = selectAllIds(lines);
  return ids.length > 0 && ids.every((id) => selected.has(id));
}

/** Marca ou desmarca uma linha (devolve um conjunto novo). Linha pendente no banco não muda nada. */
export function toggleSelected(selected: ReadonlySet<string>, line: BankReviewLineResponse): Set<string> {
  const next = new Set(selected);
  if (!isSelectable(line)) return next;
  if (next.has(line.id)) next.delete(line.id);
  else next.add(line.id);
  return next;
}

/** A seleção só com o que ainda está na lista e pode ser confirmado (depois de recarregar a revisão). */
export function pruneSelection(selected: ReadonlySet<string>, lines: readonly BankReviewLineResponse[]): Set<string> {
  const valid = new Set(selectAllIds(lines));
  return new Set([...selected].filter((id) => valid.has(id)));
}

/** Soma das selecionadas, só em reais: compra em outra moeda aparece na lista e não entra em total. */
export function selectedTotalBrl(lines: readonly BankReviewLineResponse[], selected: ReadonlySet<string>): number {
  return lines
    .filter((line) => selected.has(line.id) && isSelectable(line) && line.currency === 'BRL')
    .reduce((sum, line) => sum + line.amount, 0);
}

/**
 * Os corpos de POST review/confirm para as linhas selecionadas, na ordem da tela, em lotes. A categoria vai só
 * quando a pessoa escolheu outra que não a sugerida (sem ela o servidor usa a sugerida).
 */
export function buildConfirmBatches(
  lines: readonly BankReviewLineResponse[],
  selected: ReadonlySet<string>,
  categories: Readonly<Record<string, string>>,
  batchSize: number = CONFIRM_BATCH_SIZE,
): ConfirmBankReviewRequest[] {
  const expenses = lines
    .filter((line) => selected.has(line.id) && isSelectable(line))
    .map((line) => {
      const chosen = categories[line.id];
      return chosen && chosen !== line.suggestedCategory ? { id: line.id, category: chosen } : { id: line.id };
    });

  const size = Math.max(1, Math.floor(batchSize));
  const batches: ConfirmBankReviewRequest[] = [];
  for (let start = 0; start < expenses.length; start += size) {
    batches.push({ expenses: expenses.slice(start, start + size) });
  }
  return batches;
}

// ---------------------------------------------------------------- linhas por dia

export interface DayGroup {
  /** "AAAA-MM-DD", o dia do lançamento no Brasil, como o servidor manda. */
  readonly day: string;
  /** "dd/mm/aaaa". */
  readonly label: string;
  readonly lines: readonly BankReviewLineResponse[];
}

/** "2026-10-05" → "05/10/2026". É um dia de calendário, não um instante: nada de fuso do aparelho. */
export function dayLabel(day: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day ?? '');
  return match ? `${match[3]}/${match[2]}/${match[1]}` : day;
}

/** Dias do mais recente para o mais antigo; dentro do dia, a ordem em que o servidor mandou. */
export function groupByDay(lines: readonly BankReviewLineResponse[]): DayGroup[] {
  const byDay = new Map<string, BankReviewLineResponse[]>();
  for (const line of lines) {
    const list = byDay.get(line.day);
    if (list) list.push(line);
    else byDay.set(line.day, [line]);
  }
  return [...byDay.keys()]
    .sort((a, b) => (a < b ? 1 : a > b ? -1 : 0))
    .map((day) => ({ day, label: dayLabel(day), lines: byDay.get(day)! }));
}

export function lineTitle(line: Pick<BankReviewLineResponse, 'merchant' | 'description'>): string {
  return line.merchant?.trim() || line.description?.trim() || 'Lançamento do banco';
}

export function installmentText(line: Pick<BankReviewLineResponse, 'installmentNumber' | 'installmentTotal'>): string | null {
  return line.installmentNumber && line.installmentTotal
    ? `parcela ${line.installmentNumber} de ${line.installmentTotal}`
    : null;
}

// ---------------------------------------------------------------- mês

/** O mês em que a revisão abre: o mais recente que tem algo esperando; sem nada esperando, o mês corrente. */
export function initialReviewMonth(
  pendingByMonth: readonly BankReviewMonthResponse[] | null | undefined,
  currentMonth: string,
): string {
  const waiting = (pendingByMonth ?? []).filter((m) => m.pending > 0).map((m) => m.month);
  if (waiting.length === 0) return currentMonth;
  return waiting.reduce((latest, month) => (month > latest ? month : latest));
}

/** "2026-01" e -1 → "2025-12". */
export function shiftMonth(month: string, delta: number): string {
  const [year, number] = month.split('-').map(Number);
  const index = year * 12 + (number - 1) + delta;
  return `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}`;
}

/** "2026-10" → "outubro de 2026". */
export function monthTitle(month: string): string {
  return monthLabelFromIso(month) || month;
}

// ---------------------------------------------------------------- textos

/** Atalho da tela de Transações. null: nada esperando, o atalho não aparece. */
export function reviewShortcutLabel(pending: number | null | undefined): string | null {
  return typeof pending === 'number' && pending > 0 ? `${pending} do banco para revisar` : null;
}

/** Botão do fim do wizard. */
export function reviewDoneLabel(pending: number): string {
  if (pending <= 0) return 'Nenhuma transação para revisar agora';
  return pending === 1 ? 'Ver 1 transação para revisar' : `Ver ${pending} transações para revisar`;
}

export function confirmResultMessage(created: number, alreadyConfirmed: number): string {
  const parts: string[] = [];
  if (created > 0) parts.push(created === 1 ? '1 despesa registrada.' : `${created} despesas registradas.`);
  if (alreadyConfirmed > 0) {
    parts.push(alreadyConfirmed === 1 ? '1 já estava registrada.' : `${alreadyConfirmed} já estavam registradas.`);
  }
  return parts.length > 0 ? parts.join(' ') : 'Nada a registrar.';
}
