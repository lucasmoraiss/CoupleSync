// Sair da conta: apaga tudo do aparelho e avisa o servidor para revogar o refresh token.
import { authApiClient } from '@/services/apiClient';
import { getDevicePushToken } from '@/services/deviceToken';
import { useSessionStore } from '@/state/sessionStore';
import { clearUserData } from '@/state/userData';

/**
 * O que está no aparelho é apagado primeiro e sempre, de modo que sem internet (ou com o servidor
 * fora, ou com o armazenamento seguro falhando) o usuário sai do mesmo jeito. Só então o servidor é
 * avisado, com o token guardado antes; se a chamada falhar, o refresh token ainda vale no servidor até
 * expirar, mas já não existe no aparelho. O servidor também desregistra o token push deste aparelho
 * (se for do usuário que sai), para o celular deslogado não receber os alertas do grupo dele.
 * Há um refresh token por usuário, então isso também encerra a sessão nos outros aparelhos.
 */
export async function logout(): Promise<void> {
  const { refreshToken } = useSessionStore.getState();
  const deviceToken = getDevicePushToken().catch(() => null); // procura em paralelo; não atrasa a limpeza

  try {
    await clearUserData();
  } catch {
    // clearUserData já deixa a sessão em memória vazia antes de qualquer coisa que possa falhar.
  }

  if (!refreshToken) return;
  try {
    await authApiClient.logout(refreshToken, (await deviceToken) ?? undefined);
  } catch {
    // Sem resposta do servidor: a saída local já aconteceu.
  }
}
