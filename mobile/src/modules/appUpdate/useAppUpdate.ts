// Liga a regra (appUpdate.ts) ao servidor: última versão e versão mínima vêm de GET /api/v1/app/version.
// Não é dado do usuário nem do grupo (a rota é anônima), então a consulta não depende de sessão. Sem resposta
// (sem internet, 5xx, servidor antigo sem a rota) vale a última resposta lembrada neste aparelho
// (rememberedAnswerStore.ts); sem lembrança, a regra devolve "nenhum". A falha é sempre silenciosa.
import { useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { appApiClient } from '@/services/apiClient';
import { resolveUpdate, type ResolvedUpdate } from './appUpdate';
import { getInstalledVersion } from './installedVersion';
import { useRememberedAnswer } from './rememberedAnswerStore';

export const APP_VERSION_QUERY_KEY = ['app-version'] as const;

// O servidor guarda a resposta por uma hora; perguntar de novo a cada poucos minutos não muda nada.
const STALE_TIME_MS = 15 * 60_000;

export function useAppUpdate(): ResolvedUpdate & { refetch: () => void; refetchIfStale: () => void } {
  // Tempo limite e repetição são os padrão do app (30 s, duas repetições): a API pode levar dezenas de segundos
  // para acordar, e é nessa abertura que o aviso tem de aparecer.
  const query = useQuery({
    queryKey: APP_VERSION_QUERY_KEY,
    queryFn: async () => (await appApiClient.getVersion()).data,
    // Sem a versão instalada (módulo nativo ausente) nada seria mostrado: nem pergunta.
    enabled: getInstalledVersion() !== null,
    staleTime: STALE_TIME_MS,
  });

  const memoryLoaded = useRememberedAnswer((state) => state.loaded);
  const memoryAnswer = useRememberedAnswer((state) => state.answer);
  const loadMemory = useRememberedAnswer((state) => state.load);
  const remember = useRememberedAnswer((state) => state.remember);
  const memory = { loaded: memoryLoaded, answer: memoryAnswer };

  useEffect(() => {
    void loadMemory();
  }, [loadMemory]);

  // Cada resposta do servidor passa a ser a lembrança deste aparelho para a próxima abertura.
  useEffect(() => {
    if (query.data !== undefined) void remember(query.data);
  }, [query.data, remember]);

  return {
    ...resolveUpdate({ installed: getInstalledVersion(), live: query.data, memory }),
    refetch: () => {
      void query.refetch();
    },
    refetchIfStale: () => {
      if (query.isStale) void query.refetch();
    },
  };
}
