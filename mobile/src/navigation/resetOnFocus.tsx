// Ganchos de foco compartilhados pelas telas que ficam montadas entre visitas (abas e abas ocultas).
// A regra está em screenVisit.ts (pura, testada); aqui só a ligação com o React e o expo-router.
import React, { useCallback, useRef, useState } from 'react';
import { router, useFocusEffect } from 'expo-router';
import { initialVisitState, onScreenBlur, onScreenFocus, visitKey, type VisitState } from './screenVisit';
import { parentRouteOf, type ChildScreen } from './routes';

/** Estado de visita da tela: muda ao sair dela e ao voltar. */
export function useScreenVisit(): VisitState {
  const [visit, setVisit] = useState<VisitState>(initialVisitState);
  useFocusEffect(
    useCallback(() => {
      setVisit(onScreenFocus);
      return () => setVisit(onScreenBlur);
    }, []),
  );
  return visit;
}

/**
 * Envolve uma tela para que TODO o estado dela seja descartado a cada visita (ela é remontada com uma chave
 * nova). `paramsOf` lista os parâmetros de rota de que a tela depende: mudou o parâmetro, a tela é outra.
 */
export function resetOnFocus<P extends object>(
  Screen: React.ComponentType<P>,
  paramsOf?: (props: P) => ReadonlyArray<string | number | null | undefined>,
): React.ComponentType<P> {
  function ResetOnFocus(props: P) {
    const visit = useScreenVisit();
    return <Screen key={visitKey(visit, paramsOf?.(props))} {...props} />;
  }
  ResetOnFocus.displayName = `ResetOnFocus(${Screen.displayName ?? Screen.name ?? 'Screen'})`;
  return ResetOnFocus;
}

/** Roda `onFocus` toda vez que a tela volta a ter foco (não na primeira, quando a consulta já busca sozinha). */
export function useOnRefocus(onFocus: () => void): void {
  const first = useRef(true);
  const latest = useRef(onFocus);
  latest.current = onFocus;
  useFocusEffect(
    useCallback(() => {
      if (first.current) {
        first.current = false;
        return;
      }
      latest.current();
    }, []),
  );
}

/** Volta para a tela de onde esta é aberta, sem depender do histórico de abas. */
export function goToParent(screen: ChildScreen): void {
  router.navigate(parentRouteOf(screen) as any);
}
