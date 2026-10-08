// Pede uma sincronização do Open Finance e acompanha até terminar (ou até a tela desistir de esperar).
// Usado no passo 5 do wizard e no botão "Sincronizar agora". Todo o trabalho fica preso à época da sessão e à
// tela montada: resposta que chega depois de sair da conta, trocar de grupo ou sair da tela não muda nada.
// A regra (pedir, acompanhar, desistir de esperar) está em sync.ts (pura, testada); aqui só a ligação com o React.
import { useCallback, useEffect, useRef, useState } from 'react';
import { openFinanceApiClient } from '@/services/apiClient';
import { getSessionEpoch } from '@/state/sessionStore';
import type { SyncRunResponse } from '@/types/api';
import { SYNC_IDLE, requestAndFollowSync, type SyncOptions, type SyncRunState } from './sync';

export type { SyncPhase, SyncRunState } from './sync';

const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export function useSyncRun() {
  const [state, setState] = useState<SyncRunState>(SYNC_IDLE);
  const mounted = useRef(true);
  // Cada pedido tem um número: um pedido novo (ou a saída da tela) faz o acompanhamento anterior parar.
  const attempt = useRef(0);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      attempt.current += 1;
    };
  }, []);

  /** `onAccepted` roda uma vez, quando o servidor aceita o pedido (antes de a sincronização terminar). */
  const start = useCallback(
    (connectionId: string, options: SyncOptions, onAccepted?: (run: SyncRunResponse) => void): Promise<SyncRunResponse | null> => {
      const epoch = getSessionEpoch();
      const mine = ++attempt.current;
      return requestAndFollowSync(connectionId, options, {
        requestSync: async (id, query) => (await openFinanceApiClient.requestSync(id, query)).data,
        getSyncRun: async (runId) => (await openFinanceApiClient.getSyncRun(runId)).data,
        wait,
        now: Date.now,
        isCurrent: () => mounted.current && attempt.current === mine && getSessionEpoch() === epoch,
        onState: setState,
        onAccepted,
      });
    },
    [],
  );

  const reset = useCallback(() => {
    attempt.current += 1;
    setState(SYNC_IDLE);
  }, []);

  return { ...state, start, reset };
}
