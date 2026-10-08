// Pede uma sincronização do Open Finance e acompanha até terminar (ou até a tela desistir de esperar).
// Usado no passo 5 do wizard e no botão "Sincronizar agora". Todo o trabalho fica preso à época da sessão e à
// tela montada: resposta que chega depois de sair da conta, trocar de grupo ou sair da tela não muda nada.
import { useCallback, useEffect, useRef, useState } from 'react';
import { openFinanceApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { getSessionEpoch } from '@/state/sessionStore';
import type { SyncRunResponse } from '@/types/api';
import { RUN_POLL_MS, RUN_WAIT_LIMIT_MS, SYNC_FAILED_TEXT, isRunFinished, syncQuery, type SyncOptions } from './sync';

export type SyncPhase = 'idle' | 'working' | 'done' | 'failed' | 'stillRunning';

export interface SyncRunState {
  readonly phase: SyncPhase;
  /** A sincronização acompanhada (null antes da resposta do pedido). */
  readonly run: SyncRunResponse | null;
  /** Por que não deu para pedir (texto da API, em português), quando phase é 'failed' sem run. */
  readonly requestError: string | null;
}

const IDLE: SyncRunState = { phase: 'idle', run: null, requestError: null };

const wait = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

export function useSyncRun() {
  const [state, setState] = useState<SyncRunState>(IDLE);
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

  const start = useCallback(async (connectionId: string, options: SyncOptions): Promise<SyncRunResponse | null> => {
    const epoch = getSessionEpoch();
    const mine = ++attempt.current;
    const current = () => mounted.current && attempt.current === mine && getSessionEpoch() === epoch;

    setState({ phase: 'working', run: null, requestError: null });
    let run: SyncRunResponse;
    try {
      run = (await openFinanceApiClient.requestSync(connectionId, syncQuery(options))).data;
    } catch (error) {
      if (current()) setState({ phase: 'failed', run: null, requestError: getApiErrorMessage(error, SYNC_FAILED_TEXT) });
      return null;
    }
    if (!current()) return null;
    setState({ phase: 'working', run, requestError: null });

    const deadline = Date.now() + RUN_WAIT_LIMIT_MS;
    while (!isRunFinished(run)) {
      if (Date.now() > deadline) {
        if (current()) setState({ phase: 'stillRunning', run, requestError: null });
        return run;
      }
      await wait(RUN_POLL_MS);
      if (!current()) return null;
      try {
        run = (await openFinanceApiClient.getSyncRun(run.id)).data;
      } catch {
        // Uma consulta que falhou (rede) não encerra o acompanhamento: tenta de novo até o limite de espera.
        continue;
      }
      if (!current()) return null;
      setState({ phase: 'working', run, requestError: null });
    }

    setState({ phase: run.status === 'Done' ? 'done' : 'failed', run, requestError: null });
    return run;
  }, []);

  const reset = useCallback(() => {
    attempt.current += 1;
    setState(IDLE);
  }, []);

  return { ...state, start, reset };
}
