// A saída sozinha da tela de boas-vindas da IA para o Painel, ligada ao foco da própria tela.
import { useCallback, useRef } from 'react';
import { useFocusEffect } from 'expo-router';
import { leaveWelcomeIfDue } from './aiStatus';

/**
 * Sai (chama `leave`) quando a tela não tem o que perguntar e nada está sendo gravado — só enquanto ela está em
 * foco. A regra roda DENTRO do efeito de foco: ele roda quando a tela recebe o foco e de novo quando
 * `nothingToAsk`/`busy` mudam com ela em foco; numa instância escondida não roda.
 *
 * Não trocar por um `useEffect` que pergunta "estou em foco?" a um valor marcado por outro efeito de foco (o
 * `isFocused()` de useAiStatus): o `useFocusEffect` do expo-router só roda na passada SEGUINTE à montagem (a
 * navegação dele começa em `null`), então na montagem esse valor ainda é falso e a tela, que é remontada a cada
 * visita, ficava parada no indicador de carregamento (issue #60, revisão final 1).
 */
export function useLeaveWelcomeWhenDue(nothingToAsk: boolean, busy: boolean, leave: () => void): void {
  const latestLeave = useRef(leave);
  latestLeave.current = leave;
  useFocusEffect(
    useCallback(() => {
      leaveWelcomeIfDue(nothingToAsk, busy, { isFocused: () => true, leave: () => latestLeave.current() });
    }, [nothingToAsk, busy]),
  );
}
