// A revisão do banco (Open Finance) do grupo ativo. Como as outras consultas do grupo, some do cache ao trocar de
// grupo e ao sair da conta (queryClient.ts).
import { useQuery } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { isNoGroupError } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import type { BankReviewResponse } from '@/types/api';

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

/**
 * Quantas despesas do banco esperam a revisão (todos os meses), para o atalho da tela de Transações. Qualquer erro
 * (servidor sem a rota, sem rede) é "nenhuma": o atalho só não aparece.
 */
export function usePendingBankReviewCount(): { readonly pending: number; readonly refetch: () => void } {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  const query = useQuery<number>({
    queryKey: [...BANK_REVIEW_KEY, 'pending-count'],
    queryFn: async () => {
      try {
        return (await openFinanceApiClient.getReview()).data.pendingAllMonths ?? 0;
      } catch {
        return 0;
      }
    },
    enabled: signedIn,
    staleTime: 0,
    retry: false,
  });
  return { pending: query.data ?? 0, refetch: () => void query.refetch({ cancelRefetch: false }) };
}
