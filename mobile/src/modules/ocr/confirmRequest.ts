// Lógica pura da tela de revisão do extrato (sem React Native), testável com Jest.
import type { OcrCandidateEdit, OcrCandidateResponse, OcrConfirmRequest } from '@/types/api';

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

/** Converte o texto do campo de valor ("1.234,56") em centavos (123456). */
export function parseBRLInput(value: string): number {
  const digits = value.replace(/[^\d]/g, '');
  return digits ? Number(digits) : 0;
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
    category: c.suggestedCategory ?? '',
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
    if (changes.amountCents !== undefined && !(changes.amountCents > 0)) {
      rowErrors.amount = 'Informe um valor maior que zero.';
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
): OcrConfirmRequest {
  const originals = indexCandidates(candidates);
  const selected = rows.filter((r) => r.selected);

  const selectedIndices = selected.map((r) => r.index);
  const categoryOverrides = selected
    .filter((r) => r.category.trim().length > 0)
    .map((r) => ({ index: r.index, category: r.category.trim() }));

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
  };
}
