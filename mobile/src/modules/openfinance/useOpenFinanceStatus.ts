// O que o grupo conectou pelo Open Finance e se o servidor tem a funcionalidade. Consulta do grupo ativo: some do
// cache ao trocar de grupo e ao sair da conta, como as outras (queryClient.ts).
import { useQuery } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { isNoGroupError } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import type { OpenFinanceStatusResponse } from '@/types/api';
import { statusWhenRouteMissing } from './wizard';

export const OPEN_FINANCE_STATUS_KEY = ['openfinance-status'] as const;

export function useOpenFinanceStatus() {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  return useQuery<OpenFinanceStatusResponse>({
    queryKey: OPEN_FINANCE_STATUS_KEY,
    queryFn: async () => {
      try {
        return (await openFinanceApiClient.getStatus()).data;
      } catch (error) {
        // Servidor que ainda não tem a rota: mesma tela de "indisponível".
        const missing = statusWhenRouteMissing(error);
        if (missing) return missing;
        throw error;
      }
    },
    enabled: signedIn,
    // "Você não tem grupo" não muda ao tentar de novo.
    retry: (failureCount, error) => !isNoGroupError(error) && failureCount < 1,
  });
}
