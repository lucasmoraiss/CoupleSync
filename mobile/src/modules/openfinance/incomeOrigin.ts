// Open Finance: a origem de uma renda na tela de Rendas. Lógica pura, coberta por __tests__/incomes.test.ts.
import type { IncomeSourceResponse } from '@/types/api';

export const INCOME_FROM_BANK_TEXT = 'do banco';

/**
 * "do banco" para a renda registrada a partir de uma entrada do banco (Revisão do banco); null para as digitadas
 * e para a resposta de um servidor que ainda não manda o campo.
 */
export function incomeOriginText(source: Pick<IncomeSourceResponse, 'fromBank'>): string | null {
  return source.fromBank === true ? INCOME_FROM_BANK_TEXT : null;
}
