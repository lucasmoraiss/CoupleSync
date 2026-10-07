// Lista de transações com rolagem infinita: a API devolve páginas de tamanho fixo e o total do grupo.

import type { GetTransactionsResponse, TransactionResponse } from '@/types/api';

export const TRANSACTIONS_PAGE_SIZE = 20;

/** Próxima página a pedir, ou undefined quando já chegou ao fim. */
export function getNextTransactionsPage(
  lastPage: GetTransactionsResponse,
  allPages: readonly GetTransactionsResponse[],
): number | undefined {
  const loaded = allPages.reduce((sum, page) => sum + page.items.length, 0);
  if (lastPage.items.length === 0 || loaded >= lastPage.totalCount) return undefined;
  return lastPage.page + 1;
}

/** Todas as páginas em uma lista só; uma transação que deslocou de página entre pedidos aparece uma vez. */
export function flattenTransactionPages(
  pages: readonly GetTransactionsResponse[] | undefined,
): TransactionResponse[] {
  const seen = new Set<string>();
  const result: TransactionResponse[] = [];
  for (const page of pages ?? []) {
    for (const item of page.items) {
      if (seen.has(item.id)) continue;
      seen.add(item.id);
      result.push(item);
    }
  }
  return result;
}
