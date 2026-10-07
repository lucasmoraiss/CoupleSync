// O que fazer com um 403 COUPLE_REQUIRED ("você não tem grupo ativo"). Puro, para ser testado sem o axios real.
import { getApiErrorBody, getApiErrorCode } from './apiError';

interface ErrorWithResponse {
  readonly response?: { readonly status?: unknown };
}

export type CoupleRequiredDecision =
  /** Não é COUPLE_REQUIRED, ou é a resposta atrasada de outra sessão/outro grupo: nada a fazer. */
  | { readonly handle: false }
  /** O grupo guardado não vale mais: avisar com `message`, esquecer o grupo e ir para a escolha de grupo. */
  | { readonly handle: true; readonly message: string };

/**
 * `requestEpoch` é a época da sessão em que a requisição saiu; `currentEpoch`, a de agora. A época sobe em login,
 * logout e troca de grupo: um 403 que chega depois da troca é do grupo ANTERIOR e não pode apagar o grupo novo.
 * Sem época anotada (requisição que não passou pelo interceptador) vale o comportamento antigo: trata.
 */
export function decideCoupleRequired(
  error: unknown,
  requestEpoch: number | undefined,
  currentEpoch: number,
): CoupleRequiredDecision {
  const status = (error as ErrorWithResponse | null)?.response?.status;
  if (status !== 403 || getApiErrorCode(error) !== 'COUPLE_REQUIRED') return { handle: false };
  if (requestEpoch !== undefined && requestEpoch !== currentEpoch) return { handle: false };

  // Um corpo de erro reconhecido sempre traz a mensagem (getApiErrorBody exige), pronta para exibir.
  return { handle: true, message: getApiErrorBody(error)!.message };
}
