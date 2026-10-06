import { flattenTransactionPages, getNextTransactionsPage } from '../pagination';
import type { GetTransactionsResponse, TransactionResponse } from '@/types/api';

function tx(id: string): TransactionResponse {
  return {
    id, userId: 'u', authorName: 'A', bank: 'MANUAL', amount: 1, currency: 'BRL',
    eventTimestampUtc: '2026-10-01T12:00:00Z', description: null, merchant: null,
    category: 'OUTROS', source: 'Manual', createdAtUtc: '2026-10-01T12:00:00Z',
  };
}

function page(pageNumber: number, ids: string[], totalCount: number): GetTransactionsResponse {
  return { page: pageNumber, pageSize: 2, totalCount, items: ids.map(tx) };
}

describe('getNextTransactionsPage', () => {
  it('pede a página seguinte enquanto faltar transação', () => {
    const p1 = page(1, ['a', 'b'], 5);
    expect(getNextTransactionsPage(p1, [p1])).toBe(2);
  });

  it('para quando tudo já foi carregado', () => {
    const p1 = page(1, ['a', 'b'], 3);
    const p2 = page(2, ['c'], 3);
    expect(getNextTransactionsPage(p2, [p1, p2])).toBeUndefined();
  });

  it('para diante de uma página vazia, mesmo que o total prometa mais', () => {
    const p1 = page(1, ['a', 'b'], 10);
    const p2 = page(2, [], 10);
    expect(getNextTransactionsPage(p2, [p1, p2])).toBeUndefined();
  });

  it('lista vazia não tem próxima página', () => {
    const p1 = page(1, [], 0);
    expect(getNextTransactionsPage(p1, [p1])).toBeUndefined();
  });
});

describe('flattenTransactionPages', () => {
  it('junta as páginas na ordem e sem repetir', () => {
    const result = flattenTransactionPages([page(1, ['a', 'b'], 4), page(2, ['b', 'c'], 4)]);
    expect(result.map((t) => t.id)).toEqual(['a', 'b', 'c']);
  });

  it('sem páginas devolve lista vazia', () => {
    expect(flattenTransactionPages(undefined)).toEqual([]);
  });
});
