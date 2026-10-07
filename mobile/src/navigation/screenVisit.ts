// Telas que são abas ocultas (ou abas) NÃO são desmontadas ao sair: o estado do componente sobrevive até a
// próxima visita. Para as telas em que isso é errado (formulários, senhas, códigos, avisos de erro), cada visita
// recebe uma chave nova e a tela é montada do zero. Lógica pura aqui; o gancho React fica em resetOnFocus.tsx.

export interface VisitState {
  /** Muda sempre que o estado da tela deve ser descartado. Usada como `key` do componente. */
  readonly key: number;
  /** A tela já recebeu foco alguma vez. */
  readonly visited: boolean;
}

export const initialVisitState: VisitState = { key: 0, visited: false };

/**
 * A tela recebeu foco. Na primeira vez nada muda (ela acabou de ser montada). Nas seguintes a chave muda, para
 * que a visita comece com os parâmetros e os dados de agora — mesmo que a saída anterior não tenha sido vista.
 */
export function onScreenFocus(state: VisitState): VisitState {
  return state.visited ? { key: state.key + 1, visited: true } : { key: state.key, visited: true };
}

/** A tela perdeu o foco: o que foi digitado (senha, código, edição pela metade) é descartado na hora. */
export function onScreenBlur(state: VisitState): VisitState {
  return { key: state.key + 1, visited: state.visited };
}

/**
 * Chave de montagem de uma tela que depende de parâmetros de rota: muda quando a visita muda E quando os
 * parâmetros mudam, de modo que nenhum quadro seja desenhado com o estado de outra transação/importação.
 */
export function visitKey(visit: VisitState, params: ReadonlyArray<string | number | null | undefined> = []): string {
  return JSON.stringify([visit.key, ...params.map((p) => (p === undefined ? null : p))]);
}
