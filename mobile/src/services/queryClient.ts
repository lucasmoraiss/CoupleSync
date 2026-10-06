// Cache de consultas do app (react-query). Único, para poder ser esvaziado ao sair da conta.
import { QueryClient } from '@tanstack/react-query';
import { registerUserDataCleaner } from '@/state/userData';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 2, staleTime: 30_000 },
  },
});

registerUserDataCleaner(() => {
  queryClient.clear();
});
