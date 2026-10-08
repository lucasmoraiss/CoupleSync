// Liga o status da IA (aiStatusStore) às telas: devolve o que vale para a sessão atual e consulta o servidor de
// novo a cada vez que a tela recebe foco (as abas ficam montadas entre visitas).
import { useCallback, useRef } from 'react';
import { useFocusEffect } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import type { AiStatusResponse } from '@/types/api';
import { selectForSession, useAiStatusStore } from './aiStatusStore';

export interface AiStatusView {
  /** Último status conhecido desta pessoa neste grupo; null enquanto não carregou. */
  readonly status: AiStatusResponse | null;
  /** A última consulta falhou. Com `status` preenchido, é só um valor possivelmente antigo. */
  readonly loadFailed: boolean;
  readonly refresh: () => Promise<AiStatusResponse | null>;
}

/**
 * `onFreshWhileFocused` recebe o status que acabou de chegar do servidor, só se a tela ainda estiver em foco
 * (uma aba montada mas escondida não reage ao que outra tela consultou).
 */
export function useAiStatus(onFreshWhileFocused?: (status: AiStatusResponse) => void): AiStatusView {
  const userId = useSessionStore((state) => state.userId);
  const coupleId = useSessionStore((state) => state.coupleId);
  // Campo a campo: um seletor que devolve objeto novo faria a tela renderizar sem parar.
  const ownerUserId = useAiStatusStore((state) => state.ownerUserId);
  const ownerCoupleId = useAiStatusStore((state) => state.ownerCoupleId);
  const status = useAiStatusStore((state) => state.status);
  const loadFailed = useAiStatusStore((state) => state.loadFailed);
  const refresh = useAiStatusStore((state) => state.refresh);

  const onFresh = useRef(onFreshWhileFocused);
  onFresh.current = onFreshWhileFocused;

  useFocusEffect(
    useCallback(() => {
      let focused = true;
      void refresh().then((fresh) => {
        if (focused && fresh) onFresh.current?.(fresh);
      });
      return () => {
        focused = false;
      };
    }, [refresh]),
  );

  const mine = selectForSession({ ownerUserId, ownerCoupleId, status, loadFailed, welcomeShown: false }, userId, coupleId);
  return { status: mine.status, loadFailed: mine.loadFailed, refresh };
}
