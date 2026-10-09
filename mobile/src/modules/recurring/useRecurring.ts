// A lista de assinaturas e recorrências do grupo ativo. Como as outras consultas do grupo, some do cache ao trocar
// de grupo e ao sair da conta (queryClient.ts): nada daqui é guardado fora do cache de consultas.
import { useQuery } from '@tanstack/react-query';
import { recurringApiClient } from '@/services/apiClient';
import { isNoGroupError } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import type { RecurringChargesResponse, RecurringListResponse } from '@/types/api';
import { RECURRING_QUERY_KEY } from './recurring';

export function useRecurring() {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  return useQuery<RecurringListResponse>({
    queryKey: RECURRING_QUERY_KEY,
    queryFn: async () => (await recurringApiClient.get()).data,
    enabled: signedIn,
    // O servidor decide se recalcula; o app sempre pergunta de novo ao abrir a tela ou voltar ao Painel.
    staleTime: 0,
    retry: (failureCount, error) => !isNoGroupError(error) && failureCount < 1,
  });
}

/** As cobranças de um item, buscadas só quando ele é aberto. */
export function useRecurringCharges(id: string | null) {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  return useQuery<RecurringChargesResponse>({
    queryKey: [...RECURRING_QUERY_KEY, 'charges', id],
    queryFn: async () => (await recurringApiClient.getTransactions(id as string)).data,
    enabled: signedIn && id !== null,
    staleTime: 0,
    retry: (failureCount, error) => !isNoGroupError(error) && failureCount < 1,
  });
}
