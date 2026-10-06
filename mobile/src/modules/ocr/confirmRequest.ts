// Lógica pura da tela de revisão do extrato (sem React Native), testável com Jest.
import type {
  OcrCandidateEdit,
  OcrCandidateResponse,
  OcrConfirmRequest,
  OcrOpenImport,
} from '@/types/api';
import { toCategoryKey } from '@/modules/transactions/categories';
import { amountCentsError, centsFromDigits } from '@/utils/amount';

/** Linha editável da revisão. */
export interface ReviewRow {
  index: number;
  selected: boolean;
  description: string;
  amountCents: number;
  date: string;
  confidence: number;
  duplicateSuspected: boolean;
  category: string;
}

/** Erros de validação por índice do candidato; cada mensagem é exibida na própria linha. */
export type ReviewRowErrors = Record<number, { description?: string; amount?: string }>;

/** Converte o texto do campo de valor ("1.234,56") em centavos (123456), limitado ao teto da API. */
export function parseBRLInput(value: string): number {
  return centsFromDigits(value);
}

export function formatBRLInput(cents: number): string {
  return new Intl.NumberFormat('pt-BR', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(cents / 100);
}

export function candidateToRow(c: OcrCandidateResponse): ReviewRow {
  return {
    index: c.index,
    selected: true,
    description: c.description,
    amountCents: Math.round(c.amount * 100),
    date: c.date,
    confidence: c.confidence,
    duplicateSuspected: c.duplicateSuspected,
    // Só categoria canônica segue na linha; qualquer outra grafia vira a chave, o resto fica sem categoria.
    category: toCategoryKey(c.suggestedCategory) ?? '',
  };
}

export const DESCRIPTION_MAX_LENGTH = 512;

interface RowChanges {
  /** Descrição nova (sem espaços nas pontas), quando difere da original. */
  readonly description?: string;
  /** Valor novo em centavos, quando difere do original. */
  readonly amountCents?: number;
}

/** Compara a linha com o candidato original e devolve só o que o usuário alterou. */
function changesOf(row: ReviewRow, original: OcrCandidateResponse | undefined): RowChanges {
  if (!original) return {};
  const description = row.description.trim();
  const descriptionChanged = description !== original.description.trim();
  // Comparação em centavos: evita falso "alterado" por imprecisão de ponto flutuante
  const amountChanged = row.amountCents !== Math.round(original.amount * 100);
  return {
    ...(descriptionChanged ? { description } : {}),
    ...(amountChanged ? { amountCents: row.amountCents } : {}),
  };
}

function indexCandidates(
  candidates: readonly OcrCandidateResponse[],
): Map<number, OcrCandidateResponse> {
  return new Map(candidates.map((c) => [c.index, c]));
}

/**
 * Valida o que será enviado em candidateEdits: campos alterados de linhas selecionadas.
 * Devolve um objeto vazio quando está tudo certo.
 */
export function validateReviewRows(
  rows: readonly ReviewRow[],
  candidates: readonly OcrCandidateResponse[],
): ReviewRowErrors {
  const originals = indexCandidates(candidates);
  const errors: ReviewRowErrors = {};

  for (const row of rows) {
    if (!row.selected) continue;
    const changes = changesOf(row, originals.get(row.index));
    const rowErrors: { description?: string; amount?: string } = {};

    if (changes.description !== undefined) {
      if (changes.description.length === 0) {
        rowErrors.description = 'Informe uma descrição.';
      } else if (changes.description.length > DESCRIPTION_MAX_LENGTH) {
        rowErrors.description = `A descrição deve ter no máximo ${DESCRIPTION_MAX_LENGTH} caracteres.`;
      }
    }
    if (changes.amountCents !== undefined) {
      // Mesma regra da API: maior que zero e até R$ 999.999.999,99.
      const amountError = amountCentsError(changes.amountCents);
      if (amountError) rowErrors.amount = amountError;
    }

    if (rowErrors.description || rowErrors.amount) {
      errors[row.index] = rowErrors;
    }
  }

  return errors;
}

/**
 * Monta o corpo de POST /api/v1/ocr/{uploadId}/confirm.
 * candidateEdits leva apenas as linhas selecionadas cuja descrição ou valor foi alterado,
 * e só com os campos alterados; é omitido quando não há edição.
 */
export function buildOcrConfirmRequest(
  rows: readonly ReviewRow[],
  candidates: readonly OcrCandidateResponse[],
  options: { keepJobOpen?: boolean } = {},
): OcrConfirmRequest {
  const originals = indexCandidates(candidates);
  const selected = rows.filter((r) => r.selected);

  const selectedIndices = selected.map((r) => r.index);
  const categoryOverrides = selected
    .map((r) => ({ index: r.index, category: toCategoryKey(r.category) }))
    .filter((o): o is { index: number; category: string } => o.category !== null);

  const candidateEdits: OcrCandidateEdit[] = [];
  for (const row of selected) {
    const changes = changesOf(row, originals.get(row.index));
    if (changes.description === undefined && changes.amountCents === undefined) continue;
    candidateEdits.push({
      index: row.index,
      ...(changes.description !== undefined ? { description: changes.description } : {}),
      ...(changes.amountCents !== undefined ? { amount: changes.amountCents / 100 } : {}),
    });
  }

  return {
    selectedIndices,
    categoryOverrides,
    ...(candidateEdits.length > 0 ? { candidateEdits } : {}),
    // Só "confirmar e continuar depois" envia; sem ele a importação fecha (a chamada de sempre).
    ...(options.keepJobOpen ? { keepJobOpen: true } : {}),
  };
}

/** Quantas linhas da revisão ficaram sem seleção. */
export function unselectedCount(rows: readonly ReviewRow[]): number {
  return rows.filter((r) => !r.selected).length;
}

/** Texto da confirmação de "Confirmar e finalizar": o que não foi selecionado é descartado. */
export function finishConfirmMessage(unselected: number): string {
  return unselected === 1
    ? 'A transação não selecionada será descartada e a importação será encerrada.'
    : `As ${unselected} transações não selecionadas serão descartadas e a importação será encerrada.`;
}

/** Corpo para descartar de vez as linhas que sobraram (a importação fecha quando não resta pendente). */
export function buildDiscardRestRequest(pendingIndices: readonly number[]): OcrConfirmRequest {
  return { selectedIndices: [], discardedIndices: pendingIndices, keepJobOpen: true };
}

/** Texto de uma importação aberta na lista da tela de importar extrato. */
export function openImportSummary(item: OcrOpenImport): { title: string; pending: string } {
  const noun = item.pendingLines === 1 ? 'transação pendente' : 'transações pendentes';
  return {
    title: item.fileName && item.fileName.trim() ? item.fileName : 'Extrato',
    pending: `${item.pendingLines} de ${item.totalLines} ${noun}`,
  };
}

// ─── Estado da revisão por importação (MOB-06) ───────────────────────────────

/** Linhas da revisão de UMA importação (uploadId); `seeded` diz se já foram montadas a partir da API. */
export interface ReviewSession {
  readonly uploadId: string;
  readonly rows: ReviewRow[];
  readonly seeded: boolean;
}

export function emptyReviewSession(uploadId: string): ReviewSession {
  return { uploadId, rows: [], seeded: false };
}

/**
 * Estado que vale para o uploadId atual: se o guardado é de outra importação, volta vazio.
 * Assim a tela nunca mostra (nem confirma) linhas da importação anterior.
 */
export function sessionFor(session: ReviewSession, uploadId: string): ReviewSession {
  return session.uploadId === uploadId ? session : emptyReviewSession(uploadId);
}

/** Linhas a revisar: só as que ainda estão pendentes (as já confirmadas ou descartadas não voltam). */
export function seedReviewRows(candidates: readonly OcrCandidateResponse[]): ReviewRow[] {
  return candidates
    .filter((c) => (c.lineState ?? 'Pending') === 'Pending')
    .map(candidateToRow);
}

/** "3 entradas não importadas"; vazio quando não há entradas. */
export function creditsLabel(count: number): string {
  if (count <= 0) return '';
  return count === 1 ? '1 entrada não importada' : `${count} entradas não importadas`;
}
