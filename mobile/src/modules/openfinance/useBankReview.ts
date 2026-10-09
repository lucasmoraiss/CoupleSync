// A revisão do banco (Open Finance) do grupo ativo. Como as outras consultas do grupo, some do cache ao trocar de
// grupo e ao sair da conta (queryClient.ts).
import { useQuery } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { isNoGroupError } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import type { BankReviewResponse } from '@/types/api';
import { pendingReviewCount } from './review';

export const BANK_REVIEW_KEY = ['openfinance-review'] as const;

/** A revisão de um mês ("AAAA-MM"); sem mês, o mês corrente do servidor (usado para descobrir onde há pendências). */
export function useBankReview(month: string | null) {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  return useQuery<BankReviewResponse>({
    queryKey: [...BANK_REVIEW_KEY, month ?? 'current'],
    queryFn: async () => (await openFinanceApiClient.getReview(month ?? undefined)).data,
    enabled: signedIn,
    // A tela é remontada a cada visita e mostra a revisão como ela está no servidor agora.
    staleTime: 0,
    retry: (failureCount, error) => !isNoGroupError(error) && failureCount < 1,
  });
}

interface ReviewCounts {
  readonly pending: number;
  readonly ignored: number;
}

const NO_COUNTS: ReviewCounts = { pending: 0, ignored: 0 };

/**
 * Para o atalho da tela de Transações: quantas linhas do banco podem ser registradas (despesas e entradas) e
 * quantas estão ignoradas automaticamente (todos os meses). Qualquer erro (servidor sem a rota, sem rede) é
 * "nenhuma": o atalho só não aparece. Servidor que ainda não conta as ignoradas: zero.
 */
export function usePendingBankReviewCount(): ReviewCounts & { readonly refetch: () => void } {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  const query = useQuery<ReviewCounts>({
    queryKey: [...BANK_REVIEW_KEY, 'shortcut-counts'],
    queryFn: async () => {
      try {
        const review = (await openFinanceApiClient.getReview()).data;
        return { pending: pendingReviewCount(review), ignored: review.ignoredAllMonths ?? 0 };
      } catch {
        return NO_COUNTS;
      }
    },
    enabled: signedIn,
    staleTime: 0,
    retry: false,
  });
  const counts = query.data ?? NO_COUNTS;
  return { pending: counts.pending, ignored: counts.ignored, refetch: () => void query.refetch({ cancelRefetch: false }) };
}
