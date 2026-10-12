// Open Finance (Meu Pluggy): regras da tela "Revisão do banco". Lógica pura (sem React Native), coberta por
// __tests__/review.test.ts.
//
// Nada do banco vira transação sem alguém do grupo confirmar. O valor nunca é editável: o app manda só o id da
// linha e, quando a pessoa trocou, a categoria.
//
// Conciliação: a linha que parece uma transação que o app já tinha (possível duplicada) não entra em "Selecionar
// tudo" nem tem caixa; a pessoa responde "É a mesma" (nada é criado) ou "É outra" (vira despesa nova). O servidor
// confere de novo na hora de confirmar: a linha que passou a parecer uma compra já registrada não é confirmada, volta
// na resposta como segurada e fica na lista, marcada, esperando a resposta. O que o
// servidor ignorou sozinho (transferência entre contas do grupo, pagamento de fatura) fica na seção Ignoradas,
// saídas e entradas, cada uma com "restaurar".
import { getApiErrorMessage } from '@/services/apiError';
import { monthLabelFromIso } from '@/utils/format';
import type {
  BankReviewIgnoredMonthResponse,
  BankReviewLineResponse,
  BankReviewMatchResponse,
  BankReviewMonthResponse,
  ConfirmBankReviewRequest,
  ConfirmBankReviewResponse,
} from '@/types/api';

/** Linhas por chamada de confirmação. O servidor aceita até 500. */
export const CONFIRM_BATCH_SIZE = 200;

export const PENDING_AT_BANK_TEXT = 'Pendente no banco: poderá ser confirmado quando o banco efetivar.';
export const NO_VALUE_TEXT = 'Sem valor: não vira despesa. Você pode descartar.';
export const OTHER_CURRENCY_TEXT = 'Em outra moeda: ainda não pode virar despesa. Você pode descartar.';
export const CONFIRM_FAILED_TEXT = 'Não foi possível confirmar. Tente novamente.';

type Confirmability = Pick<BankReviewLineResponse, 'bankStatus' | 'amount' | 'currency'>;

/**
 * Por que a linha não pode ser confirmada (aparece em cinza e sem caixa); null quando pode. O banco ainda não
 * efetivou o lançamento (o servidor recusa confirmá-lo), ele veio sem valor, ou está em outra moeda (o servidor
 * pula os dois: tudo o que o app lista e soma é em reais).
 */
export function unselectableReason(line: Confirmability): string | null {
  if (line.bankStatus === 'Pending') return PENDING_AT_BANK_TEXT;
  if (!(line.amount > 0)) return NO_VALUE_TEXT;
  if (line.currency !== 'BRL') return OTHER_CURRENCY_TEXT;
  return null;
}

/** A linha parece uma transação que o app já tinha: espera "É a mesma" ou "É outra". */
export function hasSuspectedDuplicate(line: Pick<BankReviewLineResponse, 'match'>): boolean {
  return !!line.match;
}

/**
 * A linha tem caixa e pode ir em "Confirmar selecionadas": pode virar despesa e não tem possível duplicada (essa
 * só entra pela resposta da pessoa, uma a uma).
 */
export function isSelectable(line: Confirmability & Pick<BankReviewLineResponse, 'match'>): boolean {
  return unselectableReason(line) === null && !hasSuspectedDuplicate(line);
}

/**
 * "Selecionar tudo": todas as despesas da lista, menos as pendentes no banco, as sem valor, as em outra moeda e as
 * que têm possível duplicada.
 */
export function selectAllIds(lines: readonly BankReviewLineResponse[]): string[] {
  return lines.filter(isSelectable).map((line) => line.id);
}

export function allSelected(lines: readonly BankReviewLineResponse[], selected: ReadonlySet<string>): boolean {
  const ids = selectAllIds(lines);
  return ids.length > 0 && ids.every((id) => selected.has(id));
}

/** Marca ou desmarca uma linha (devolve um conjunto novo). Linha que não pode ser confirmada não muda nada. */
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

/** Soma das selecionadas. Só reais: compra em outra moeda aparece na lista, não é selecionável e não entra em total. */
export function selectedTotalBrl(lines: readonly BankReviewLineResponse[], selected: ReadonlySet<string>): number {
  return lines
    .filter((line) => selected.has(line.id) && isSelectable(line) && line.currency === 'BRL')
    .reduce((sum, line) => sum + line.amount, 0);
}

/**
 * Os corpos de POST review/confirm para as linhas selecionadas, na ordem da tela, em lotes. A categoria vai só
 * quando a pessoa escolheu outra que não a sugerida (sem ela o servidor usa a sugerida). Todo lote diz ao servidor
 * que ninguém respondeu sobre duplicadas nessas linhas (`holdPossibleDuplicates`): a que tiver ou ganhar possível
 * duplicada é segurada, não confirmada.
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
    batches.push({ expenses: expenses.slice(start, start + size), holdPossibleDuplicates: true });
  }
  return batches;
}

export interface ConfirmInBatchesDeps {
  /** POST review/confirm de um lote; devolve o corpo da resposta. */
  readonly confirm: (batch: ConfirmBankReviewRequest) => Promise<ConfirmBankReviewResponse>;
  /** false quando a sessão em que o pedido começou não é mais a atual (saiu da conta, trocou de grupo). */
  readonly isCurrent: () => boolean;
}

/**
 * `done`: todos os lotes entraram. `failed`: um lote falhou e os seguintes não foram enviados (o que já entrou
 * fica; reenviar é seguro, o servidor não registra duas vezes). `stale`: a sessão mudou no meio; nada a mostrar.
 * `heldIds` (só quando há alguma): as linhas que o servidor segurou por parecerem uma compra já registrada; não
 * foram confirmadas e continuam na revisão.
 */
export type ConfirmInBatchesOutcome =
  | { readonly kind: 'done'; readonly text: string; readonly heldIds?: readonly string[] }
  | { readonly kind: 'failed'; readonly text: string; readonly heldIds?: readonly string[] }
  | { readonly kind: 'stale' };

/**
 * Aviso das linhas que o servidor segurou ao confirmar: parecem compras já registradas e esperam "É a mesma" ou
 * "É outra". null: nenhuma.
 */
export function heldDuplicatesText(count: number): string | null {
  if (!(count > 0)) return null;
  return count === 1
    ? '1 lançamento parece uma compra que você já registrou. Responda nele: é a mesma ou é outra?'
    : `${count} lançamentos parecem compras que você já registrou. Responda em cada um: é a mesma ou é outra?`;
}

/** As categorias escolhidas que continuam valendo depois de confirmar: só as das linhas seguradas (elas ficam na lista). */
export function chosenOfHeld(
  chosen: Readonly<Record<string, string>>,
  heldIds: readonly string[] | null | undefined,
): Record<string, string> {
  const kept: Record<string, string> = {};
  for (const id of heldIds ?? []) {
    if (chosen[id]) kept[id] = chosen[id];
  }
  return kept;
}

/**
 * O aviso do que uma confirmação fez. As seguradas não são despesas registradas: são contadas à parte, no fim. Só
 * seguradas: só o aviso delas.
 */
function confirmOutcomeText(created: number, already: number, skipped: number, otherCurrency: number, held: number): string {
  const heldText = heldDuplicatesText(held);
  if (!heldText) return confirmResultMessage(created, already, skipped, otherCurrency);
  return created + already + skipped > 0
    ? `${confirmResultMessage(created, already, skipped, otherCurrency)} ${heldText}`
    : heldText;
}

/**
 * "Confirmar selecionadas": um lote por vez, na ordem. Cada chamada é tudo ou nada no servidor. Para no primeiro
 * lote que falhar e diz quantas despesas entraram antes dele e por que o restante não entrou.
 */
export async function confirmInBatches(
  batches: readonly ConfirmBankReviewRequest[],
  deps: ConfirmInBatchesDeps,
): Promise<ConfirmInBatchesOutcome> {
  let created = 0;
  let already = 0;
  let skipped = 0;
  let otherCurrency = 0;
  const held: string[] = [];
  const heldIds = () => (held.length > 0 ? { heldIds: held } : {});
  try {
    for (const batch of batches) {
      const result = await deps.confirm(batch);
      if (!deps.isCurrent()) return { kind: 'stale' };
      created += result.created.length;
      already += result.alreadyConfirmed;
      skipped += result.skipped?.length ?? 0;
      otherCurrency += result.skippedOtherCurrency?.length ?? 0;
      held.push(...(result.heldAsPossibleDuplicates ?? []));
    }
  } catch (err) {
    if (!deps.isCurrent()) return { kind: 'stale' };
    const reason = getApiErrorMessage(err, CONFIRM_FAILED_TEXT);
    return {
      kind: 'failed',
      text:
        created + already + held.length > 0
          ? `${confirmOutcomeText(created, already, skipped, otherCurrency, held.length)} O restante não entrou: ${reason}`
          : reason,
      ...heldIds(),
    };
  }
  return { kind: 'done', text: confirmOutcomeText(created, already, skipped, otherCurrency, held.length), ...heldIds() };
}

// ---------------------------------------------------------------- possíveis duplicadas

export const ANSWER_SAME_FAILED_TEXT = 'Não foi possível marcar como a mesma. Tente novamente.';
export const ANSWER_OTHER_FAILED_TEXT = 'Não foi possível registrar como outra despesa. Tente novamente.';

/** 'same': "É a mesma" (a transação que já existia fica; nada é criado). 'other': "É outra" (despesa nova). */
export type DuplicateAnswer = 'same' | 'other';

export function suspectedDuplicateCount(lines: readonly BankReviewLineResponse[]): number {
  return lines.filter(hasSuspectedDuplicate).length;
}

/**
 * Quantas possíveis duplicadas já podem ser respondidas ("É a mesma" / "É outra" habilitados). É o número do aviso
 * do topo: a que ainda está pendente no banco aparece na lista com os botões desabilitados e não precisa de
 * ninguém por enquanto.
 */
export function answerableDuplicateCount(lines: readonly BankReviewLineResponse[]): number {
  return lines.filter(canAnswerDuplicate).length;
}

/** Aviso do topo da revisão. null: nenhuma possível duplicada a responder, sem aviso. */
export function duplicateNoticeText(count: number): string | null {
  if (!(count > 0)) return null;
  return count === 1 ? '1 possível duplicada precisa de você' : `${count} possíveis duplicadas precisam de você`;
}

const MATCH_SOURCE_LABELS: Readonly<Record<string, string>> = {
  Manual: 'lançamento manual',
  Notification: 'notificação',
  OcrImport: 'extrato importado',
};

/** "2026-10-03" → "03/10". É um dia de calendário, não um instante; o que não vier nesse formato aparece como veio. */
function shortDay(day: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(day ?? '');
  return match ? `${match[3]}/${match[2]}` : day;
}

/** "Parece a mesma de: Padaria · R$ 25,00 · notificação de 03/10". O valor sai na moeda da transação. */
export function matchText(match: BankReviewMatchResponse): string {
  const title = match.merchant?.trim() || match.description?.trim() || 'transação já registrada';
  const source = MATCH_SOURCE_LABELS[match.source] ?? 'registro';
  return `Parece a mesma de: ${title} · ${lineAmountText(match.amount, match.currency)} · ${source} de ${shortDay(match.day)}`;
}

/**
 * "É a mesma" e "É outra" só valem para a linha com possível duplicada que pode virar despesa: o servidor recusa
 * as duas respostas enquanto o banco não efetivou o lançamento.
 */
export function canAnswerDuplicate(line: BankReviewLineResponse): boolean {
  return hasSuspectedDuplicate(line) && unselectableReason(line) === null;
}

/**
 * O corpo de POST review/confirm para a resposta da pessoa. "É a mesma": só o id (nada é criado, a categoria da
 * tela não vai). "É outra": o id em `asNew`; se a pessoa escolheu outra categoria que não a sugerida, vai como uma
 * despesa comum com essa categoria (o servidor confirma do mesmo jeito e tira a marca de possível duplicada).
 */
export function answerDuplicateRequest(
  answer: DuplicateAnswer,
  line: BankReviewLineResponse,
  categories: Readonly<Record<string, string>>,
): ConfirmBankReviewRequest {
  if (answer === 'same') return { reconcile: [{ id: line.id }] };
  const chosen = categories[line.id];
  return chosen && chosen !== line.suggestedCategory
    ? { expenses: [{ id: line.id, category: chosen }] }
    : { asNew: [line.id] };
}

export function answerDuplicateResultMessage(answer: DuplicateAnswer, result: ConfirmBankReviewResponse): string {
  if (answer === 'same') {
    return (result.reconciled?.length ?? 0) > 0
      ? 'Marcada como a mesma: nenhuma despesa nova foi criada.'
      : 'Nada foi alterado.';
  }
  // "É outra" com outra categoria vai como despesa comum: se a marca tinha sumido e o servidor a achou de novo, ele segura.
  return confirmOutcomeText(
    result.created.length,
    result.alreadyConfirmed,
    result.skipped?.length ?? 0,
    result.skippedOtherCurrency?.length ?? 0,
    result.heldAsPossibleDuplicates?.length ?? 0,
  );
}

export const SAME_KEPT_CATEGORY_TEXT =
  'A categoria escolhida não foi usada: a transação que já estava no app manteve a dela.';

/**
 * O aviso depois da resposta. Igual ao de `answerDuplicateResultMessage`; em "É a mesma" com outra categoria
 * escolhida no chip, acrescenta que ela não foi usada (nada é criado, a transação que já existia fica como está),
 * para a escolha não sumir em silêncio.
 */
export function answerDuplicateNotice(
  answer: DuplicateAnswer,
  line: BankReviewLineResponse,
  categories: Readonly<Record<string, string>>,
  result: ConfirmBankReviewResponse,
): string {
  const message = answerDuplicateResultMessage(answer, result);
  const chosen = categories[line.id];
  const reconciled = (result.reconciled?.length ?? 0) > 0;
  return answer === 'same' && reconciled && chosen && chosen !== line.suggestedCategory
    ? `${message} ${SAME_KEPT_CATEGORY_TEXT}`
    : message;
}

// ---------------------------------------------------------------- ignoradas

/** Por que o servidor ignorou a linha sozinho. */
export function autoReasonText(reason: string | null | undefined): string {
  if (reason === 'Transfer') return 'Transferência entre contas do grupo';
  if (reason === 'BillPayment') return 'Pagamento de fatura do cartão';
  return 'Ignorada automaticamente';
}

/** "05/10/2026 · R$ 500,00 · Transferência entre contas do grupo". O valor sai na moeda da linha. */
export function ignoredLineText(line: Pick<BankReviewLineResponse, 'day' | 'amount' | 'currency' | 'type' | 'autoReason'>): string {
  const amount = lineAmountText(line.amount, line.currency);
  return `${dayLabel(line.day)} · ${line.type === 'Credit' ? `entrada de ${amount}` : amount} · ${autoReasonText(line.autoReason)}`;
}

/**
 * Toda linha ignorada pode ser restaurada, a entrada também: o servidor ignora entradas sozinho (dinheiro que veio
 * de alguém do grupo, a perna de entrada de uma transferência) e pode errar. A saída restaurada volta para as
 * despesas desta tela, à vista. A entrada restaurada deixa de ser ignorada mas some daqui, porque esta tela só
 * lista despesas: é esse o aviso. null: nada a avisar.
 */
export function restoredNoticeText(line: Pick<BankReviewLineResponse, 'type'>): string | null {
  return line.type === 'Credit'
    ? 'Entrada restaurada: ela deixou de ser ignorada. Esta lista é só de despesas, por isso ela não aparece aqui.'
    : null;
}

/** O que o botão "restaurar" de uma linha diz em voz alta. */
export function restoreLabel(line: Pick<BankReviewLineResponse, 'type' | 'merchant' | 'description'>): string {
  return line.type === 'Credit' ? `Restaurar entrada ${lineTitle(line)}` : `Restaurar ${lineTitle(line)}`;
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

/**
 * O valor de uma linha, na moeda dela. Só o que é BRL sai como reais: sem moeda, o número sem símbolo; moeda que
 * o aparelho não conhece, o código dela e o número.
 */
export function lineAmountText(amount: number, currency: string | null | undefined): string {
  const code = (currency ?? '').trim();
  const plain = amount.toFixed(2).replace('.', ',');
  if (!code) return plain;
  try {
    return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: code }).format(amount);
  } catch {
    return `${code} ${plain}`;
  }
}

export function installmentText(line: Pick<BankReviewLineResponse, 'installmentNumber' | 'installmentTotal'>): string | null {
  return line.installmentNumber && line.installmentTotal
    ? `parcela ${line.installmentNumber} de ${line.installmentTotal}`
    : null;
}

// ---------------------------------------------------------------- mês

/**
 * O mês em que a revisão abre: o mais recente que tem algo esperando; sem nada esperando, o mais recente que tem
 * linhas ignoradas (é para elas que o atalho leva quando nada espera); sem nenhum dos dois, o mês corrente.
 */
export function initialReviewMonth(
  pendingByMonth: readonly BankReviewMonthResponse[] | null | undefined,
  currentMonth: string,
  ignoredByMonth?: readonly BankReviewIgnoredMonthResponse[] | null,
): string {
  const latestOf = (months: readonly string[]) => months.reduce((latest, month) => (month > latest ? month : latest));
  const waiting = (pendingByMonth ?? []).filter((m) => m.pending > 0).map((m) => m.month);
  if (waiting.length > 0) return latestOf(waiting);
  const ignored = (ignoredByMonth ?? []).filter((m) => m.ignored > 0).map((m) => m.month);
  return ignored.length > 0 ? latestOf(ignored) : currentMonth;
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

/**
 * Atalho da tela de Transações. Com algo esperando, o texto de sempre. Sem nada esperando e com linhas que o
 * servidor ignorou sozinho (transferência entre contas do grupo, pagamento de fatura), o atalho leva até elas: é
 * na revisão que se confere e se restaura o que foi ignorado por engano. null: nada dos dois, o atalho não aparece.
 */
export function reviewShortcutLabel(pending: number | null | undefined, ignored?: number | null): string | null {
  if (typeof pending === 'number' && pending > 0) return `${pending} do banco para revisar`;
  if (typeof ignored === 'number' && ignored > 0) {
    return ignored === 1 ? '1 ignorada do banco para conferir' : `${ignored} ignoradas do banco para conferir`;
  }
  return null;
}

/**
 * A tela do Open Finance mostra o acesso fixo "Revisão do banco" quando o grupo tem alguma conexão (também a
 * desconectada: o que ela trouxe continua na revisão). Não depende de haver pendência.
 */
export function hasReviewAccess(status: { readonly connections?: readonly unknown[] | null } | null | undefined): boolean {
  return (status?.connections?.length ?? 0) > 0;
}

/** Botão do fim do wizard. */
export function reviewDoneLabel(pending: number): string {
  if (pending <= 0) return 'Nenhuma transação para revisar agora';
  return pending === 1 ? 'Ver 1 transação para revisar' : `Ver ${pending} transações para revisar`;
}

/**
 * `skipped`: linhas que o servidor pulou (continuam na revisão); `otherCurrency`: quantas delas por estarem em
 * outra moeda. As demais são as sem valor.
 */
export function confirmResultMessage(
  created: number,
  alreadyConfirmed: number,
  skipped: number = 0,
  otherCurrency: number = 0,
): string {
  const parts: string[] = [];
  if (created > 0) parts.push(created === 1 ? '1 despesa registrada.' : `${created} despesas registradas.`);
  if (alreadyConfirmed > 0) {
    parts.push(alreadyConfirmed === 1 ? '1 já estava registrada.' : `${alreadyConfirmed} já estavam registradas.`);
  }
  const noValue = Math.max(0, skipped - otherCurrency);
  if (noValue > 0) {
    parts.push(noValue === 1 ? '1 sem valor continua na revisão.' : `${noValue} sem valor continuam na revisão.`);
  }
  if (otherCurrency > 0) {
    parts.push(
      otherCurrency === 1 ? '1 em outra moeda continua na revisão.' : `${otherCurrency} em outra moeda continuam na revisão.`,
    );
  }
  return parts.length > 0 ? parts.join(' ') : 'Nada a registrar.';
}
