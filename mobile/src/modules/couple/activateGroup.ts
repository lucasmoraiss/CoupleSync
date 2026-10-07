// Troca do grupo ativo no aparelho (trocar, sair para outro grupo, criar ou entrar em mais um). A ordem é o que
// impede dado de um grupo de aparecer, ou de ser gravado, no outro; por isso fica aqui, pura e testada
// (__tests__/activateGroup.test.ts), e as telas só chamam `applyGroupSession` (groupSession.ts).

export interface GroupSession {
  readonly accessToken: string;
  /** Ausente/vazio = mantém o refresh token guardado (criar/entrar só devolvem um quando o usuário não tinha). */
  readonly refreshToken?: string | null;
  /** null = o usuário ficou sem grupo ativo. */
  readonly coupleId: string | null;
}

export interface ActivateGroupDeps {
  /** Descarta notificações bancárias capturadas que ainda não foram enviadas. */
  dropPendingCaptures(): void;
  /** Guarda os tokens e o grupo novos (a partir daqui toda requisição sai pelo grupo novo). */
  saveSession(session: GroupSession): Promise<void>;
  /** Cancela as consultas em andamento. */
  cancelQueries(): Promise<void>;
  /** Esvazia tudo o que veio do grupo anterior (cache de consultas e estado de tela ligado ao grupo). */
  clearGroupData(): void;
  /** Remonta as telas, que então buscam tudo de novo. */
  remountScreens(): void;
}

/**
 * 1. O que foi capturado para o grupo anterior e ainda não saiu do aparelho é descartado: nunca é lançado no
 *    grupo novo.
 * 2. A sessão passa para o grupo novo.
 * 3. Só então as consultas em andamento são canceladas e o cache é esvaziado: uma resposta pedida com o token
 *    do grupo anterior não entra no cache depois da limpeza, e nada é buscado de novo com o token antigo.
 * 4. As telas são remontadas por último, com o cache vazio e o token novo. Só quando há um grupo novo: quem ficou
 *    sem grupo ativo vai para a escolha de grupo, e remontar as telas de dados só as faria buscar sem grupo.
 */
export async function activateGroup(session: GroupSession, deps: ActivateGroupDeps): Promise<void> {
  deps.dropPendingCaptures();
  await deps.saveSession(session);
  await deps.cancelQueries();
  deps.clearGroupData();
  if (session.coupleId) {
    deps.remountScreens();
  }
}
