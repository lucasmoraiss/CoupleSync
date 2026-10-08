// Liga o status da IA (aiStatusStore) às telas: devolve o que vale para a sessão atual e consulta o servidor de
// novo a cada vez que a tela recebe foco (as abas ficam montadas entre visitas).
import { useCallback, useEffect, useRef } from 'react';
import { useFocusEffect } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import type { AiStatusResponse } from '@/types/api';
import { currentAiStatus, selectForSession, selectLoadForSession, useAiStatusStore } from './aiStatusStore';

export interface AiStatusView {
  /** Último status conhecido desta pessoa neste grupo (do servidor ou guardado no aparelho); null se não há nenhum. */
  readonly status: AiStatusResponse | null;
  /** A última consulta falhou. Com `status` preenchido, é só um valor possivelmente antigo. */
  readonly loadFailed: boolean;
  /** A consulta falhou e outra tentativa já está marcada. */
  readonly retrying: boolean;
  readonly refresh: () => Promise<AiStatusResponse | null>;
  /** A tela que chamou está em foco agora? */
  readonly isFocused: () => boolean;
}

/**
 * `onFreshWhileFocused` recebe cada status que chega do servidor — pela consulta do foco, por uma nova tentativa
 * depois de uma falha ou por uma escrita — só enquanto a tela estiver em foco (uma aba montada mas escondida não
 * reage ao que outra tela consultou). O valor guardado no aparelho não conta como resposta do servidor.
 */
export function useAiStatus(onFreshWhileFocused?: (status: AiStatusResponse) => void): AiStatusView {
  const userId = useSessionStore((state) => state.userId);
  const coupleId = useSessionStore((state) => state.coupleId);
  // Campo a campo: um seletor que devolve objeto novo faria a tela renderizar sem parar.
  const ownerUserId = useAiStatusStore((state) => state.ownerUserId);
  const ownerCoupleId = useAiStatusStore((state) => state.ownerCoupleId);
  const status = useAiStatusStore((state) => state.status);
  const loadFailed = useAiStatusStore((state) => state.loadFailed);
  const fromStorage = useAiStatusStore((state) => state.fromStorage);
  const retrying = useAiStatusStore((state) => state.retrying);
  const freshCount = useAiStatusStore((state) => state.freshCount);
  const refresh = useAiStatusStore((state) => state.refresh);

  const onFresh = useRef(onFreshWhileFocused);
  onFresh.current = onFreshWhileFocused;
  const focused = useRef(false);
  const isFocused = useCallback(() => focused.current, []);

  useFocusEffect(
    useCallback(() => {
      focused.current = true;
      void refresh();
      return () => {
        focused.current = false;
      };
    }, [refresh]),
  );

  // Cada resposta do servidor (a contagem sobe), venha de onde vier.
  useEffect(() => {
    if (freshCount === 0 || !focused.current) return;
    const fresh = currentAiStatus();
    if (fresh) onFresh.current?.(fresh);
  }, [freshCount]);

  const mine = selectForSession({ ownerUserId, ownerCoupleId, status, loadFailed, welcomeShown: false }, userId, coupleId);
  const load = selectLoadForSession({ ownerUserId, ownerCoupleId, fromStorage, retrying }, userId, coupleId);
  return { status: mine.status, loadFailed: mine.loadFailed, retrying: load.retrying, refresh, isFocused };
}
