// As contas e os cartões que o grupo conectou pelo Open Finance, com saldo e faturas. Consulta do grupo ativo:
// some do cache ao trocar de grupo e ao sair da conta, como as outras (queryClient.ts).
import { useQuery } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { isNoGroupError } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import type { BankAccountsOverviewResponse } from '@/types/api';

export const BANK_ACCOUNTS_KEY = ['openfinance-accounts'] as const;

export function useBankAccounts() {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  return useQuery<BankAccountsOverviewResponse>({
    queryKey: BANK_ACCOUNTS_KEY,
    queryFn: async () => (await openFinanceApiClient.getAccounts()).data,
    enabled: signedIn,
    // A tela é remontada a cada visita e mostra os saldos como o servidor os tem agora.
    staleTime: 0,
    retry: (failureCount, error) => !isNoGroupError(error) && failureCount < 1,
  });
}
