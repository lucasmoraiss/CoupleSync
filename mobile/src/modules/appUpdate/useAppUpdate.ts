// Liga a regra (appUpdate.ts) ao servidor: última versão e versão mínima vêm de GET /api/v1/app/version.
// Não é dado do usuário nem do grupo (a rota é anônima), então a consulta não depende de sessão. Sem resposta
// (sem internet, 5xx, servidor antigo sem a rota) `data` fica indefinido e a regra devolve "nenhum".
import { useQuery } from '@tanstack/react-query';
import { appApiClient } from '@/services/apiClient';
import { appUpdateState, type AppUpdateState } from './appUpdate';
import { getInstalledVersion } from './installedVersion';

export const APP_VERSION_QUERY_KEY = ['app-version'] as const;

// O servidor guarda a resposta por uma hora; perguntar de novo a cada poucos minutos não muda nada.
const STALE_TIME_MS = 15 * 60_000;

export function useAppUpdate(): AppUpdateState & { refetchIfStale: () => void } {
  const query = useQuery({
    queryKey: APP_VERSION_QUERY_KEY,
    queryFn: async () => (await appApiClient.getVersion()).data,
    // Sem a versão instalada (módulo nativo ausente) nada seria mostrado: nem pergunta.
    enabled: getInstalledVersion() !== null,
    retry: false,
    staleTime: STALE_TIME_MS,
  });
  return {
    ...appUpdateState(getInstalledVersion(), query.data),
    refetchIfStale: () => {
      if (query.isStale) void query.refetch();
    },
  };
}
