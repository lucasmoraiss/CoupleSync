// Sessão expirada (refresh recusado): além de limpar o aparelho, tenta desregistrar o token push deste aparelho
// no servidor, para o celular não continuar recebendo os alertas do grupo de quem perdeu a sessão.
// Sem dependência de React Native: as ações vêm por parâmetro, para testar com Jest.
export interface SessionExpiryDeps {
  getRefreshToken(): string | null;
  /** Nunca rejeita; null quando o aparelho não tem token push. */
  getDevicePushToken(): Promise<string | null>;
  /** Apaga sessão, caches e estados do usuário (sessão em memória primeiro). */
  clearUserData(): Promise<void>;
  /** Avisa o usuário e leva ao login. */
  notifySignedOut(): void;
  /** POST /auth/logout com o refresh token e o token push. Pode rejeitar. */
  revokeOnServer(refreshToken: string, devicePushToken: string): Promise<unknown>;
}

export async function handleSessionExpired(deps: SessionExpiryDeps): Promise<void> {
  // Anota antes de limpar: depois da limpeza o refresh token já não existe no aparelho.
  const refreshToken = deps.getRefreshToken();
  const devicePushToken = deps.getDevicePushToken().catch(() => null); // em paralelo; não atrasa a limpeza

  await deps.clearUserData();
  deps.notifySignedOut();

  if (!refreshToken) return;
  // Melhor esforço, sem atrasar nem impedir a saída: o servidor só desregistra o token se o refresh token
  // ainda for conhecido por ele; se não for, o registro some quando outro usuário usar este aparelho.
  void devicePushToken
    .then((token) => (token ? deps.revokeOnServer(refreshToken, token) : undefined))
    .catch(() => undefined);
}
