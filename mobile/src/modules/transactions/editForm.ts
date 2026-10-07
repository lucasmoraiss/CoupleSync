// Formulário de edição de transação: lógica pura (sem React), para que a regra fique testada.
//
// A tela de edição é uma aba oculta e continua montada entre visitas. O formulário guarda de qual transação
// são os valores digitados (transactionId + a "foto" dos valores originais): se a tela passa a mostrar outra
// transação, o formulário antigo é reconhecido como velho e NUNCA é enviado para o id novo.
import { toCategoryKey } from '@/modules/transactions/categories';
import { parseAmountText } from '@/utils/amount';
import { formatBrazilDate, formatBrazilTime, parseBrazilDateTime } from '@/utils/brazilDateTime';

/** O que a lista manda pela rota (tudo texto; a API não tem leitura por id). */
export interface EditRouteParams {
  id?: string;
  amount?: string;
  description?: string;
  merchant?: string;
  category?: string;
  eventTimestampUtc?: string;
}

/** A transação como está gravada, no formato que o formulário compara. */
export interface EditOriginal {
  id: string;
  amount: number;
  description: string;
  merchant: string;
  category: string;
  eventTimestampUtc: string;
  dateText: string;
  timeText: string;
}

/** O que o usuário está digitando, preso à transação de onde saiu. */
export interface EditFormState {
  /** Id da transação a que estes campos pertencem. */
  transactionId: string;
  /** Assinatura dos valores originais usados para preencher os campos. */
  originalSignature: string;
  amountText: string;
  merchant: string;
  description: string;
  dateText: string;
  timeText: string;
  category: string;
}

export interface UpdateBody {
  amount?: number;
  description?: string;
  merchant?: string;
  category?: string;
  eventTimestampUtc?: string;
}

export type EditSubmission =
  /** O formulário é de outra transação (ou de valores antigos da mesma): nada pode ser enviado. */
  | { kind: 'stale' }
  | { kind: 'invalid'; title: string; message: string }
  | { kind: 'unchanged' }
  | { kind: 'update'; id: string; body: UpdateBody };

export function formatAmountText(amount: number): string {
  return amount.toFixed(2).replace('.', ',');
}

export function originalFromParams(params: EditRouteParams, nowIso: string = new Date().toISOString()): EditOriginal {
  const eventTimestampUtc = params.eventTimestampUtc || nowIso;
  return {
    id: params.id ?? '',
    amount: Number(params.amount),
    description: params.description ?? '',
    merchant: params.merchant ?? '',
    category: toCategoryKey(params.category ?? '') ?? params.category ?? '',
    eventTimestampUtc,
    dateText: formatBrazilDate(eventTimestampUtc),
    timeText: formatBrazilTime(eventTimestampUtc),
  };
}

/** Identifica "esta transação com estes valores". Muda quando a tela passa a editar outra coisa. */
export function originalSignature(original: EditOriginal): string {
  return JSON.stringify([
    original.id,
    original.amount,
    original.description,
    original.merchant,
    original.category,
    original.eventTimestampUtc,
  ]);
}

export function createEditForm(original: EditOriginal): EditFormState {
  return {
    transactionId: original.id,
    originalSignature: originalSignature(original),
    amountText: formatAmountText(original.amount),
    merchant: original.merchant,
    description: original.description,
    dateText: original.dateText,
    timeText: original.timeText,
    category: original.category,
  };
}

/** O formulário foi preenchido a partir exatamente desta transação, com estes valores? */
export function formBelongsTo(form: EditFormState, original: EditOriginal): boolean {
  return form.transactionId === original.id && form.originalSignature === originalSignature(original);
}

/** O formulário válido para a transação da tela: o próprio, ou um novo quando o guardado é de outra. */
export function formFor(form: EditFormState, original: EditOriginal): EditFormState {
  return formBelongsTo(form, original) ? form : createEditForm(original);
}

/**
 * O que enviar ao salvar: só os campos que mudaram em relação à transação original.
 * Um formulário que não pertence à transação (tela reaproveitada) devolve 'stale' e nada é enviado.
 */
export function buildEditSubmission(form: EditFormState, original: EditOriginal): EditSubmission {
  if (!original.id || !formBelongsTo(form, original)) return { kind: 'stale' };

  const body: UpdateBody = {};

  const parsed = parseAmountText(form.amountText);
  if (!parsed.ok) return { kind: 'invalid', title: 'Valor inválido', message: parsed.message };
  if (parsed.value !== original.amount) body.amount = parsed.value;

  if (form.merchant.trim() !== original.merchant.trim()) body.merchant = form.merchant.trim();
  if (form.description.trim() !== original.description.trim()) body.description = form.description.trim();

  if (form.dateText.trim() !== original.dateText || form.timeText.trim() !== original.timeText) {
    const when = parseBrazilDateTime(form.dateText, form.timeText);
    if (!when.ok) return { kind: 'invalid', title: 'Data inválida', message: when.message };
    body.eventTimestampUtc = when.iso;
  }

  if (form.category !== original.category) body.category = form.category;

  if (Object.keys(body).length === 0) return { kind: 'unchanged' };
  return { kind: 'update', id: form.transactionId, body };
}
