// Cache de consultas do app (react-query). Único, para poder ser esvaziado ao sair da conta.
import { QueryClient } from '@tanstack/react-query';
import { registerUserDataCleaner } from '@/state/userData';

export function createAppQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: 2, staleTime: 30_000 },
    },
  });
}

export const queryClient = createAppQueryClient();

// Consulta do próprio grupo (tela do grupo): fica ao limpar o cache do grupo, senão seria refeita em laço.
const GROUP_QUERY_KEY = 'couple-me';

/** Esquece tudo o que veio do grupo antigo (o usuário não tem mais grupo), exceto a consulta do próprio grupo. */
export function clearGroupScopedQueries(client: QueryClient): void {
  client.removeQueries({ predicate: (query) => query.queryKey[0] !== GROUP_QUERY_KEY });
}

// Primeiro cancela o que está em andamento (a resposta que chegar depois não pode entrar no cache), depois esvazia.
registerUserDataCleaner(async () => {
  await queryClient.cancelQueries();
  queryClient.clear();
});
