// Coordenação da renovação de sessão (refresh token) do cliente HTTP.
// Módulo sem dependência de React Native (só axios), para ser testável com Jest.
//
// Regras:
//  - o primeiro 401 de uma requisição dispara POST /api/v1/auth/refresh;
//  - requisições simultâneas com 401 esperam o MESMO refresh (o backend rotaciona o
//    refresh token a cada uso; um segundo refresh em paralelo usaria um token já inválido);
//  - a requisição original é repetida uma única vez com o token novo;
//  - se o refresh for recusado, ou a repetição devolver 401, a sessão é encerrada;
//  - rotas anônimas de /api/v1/auth/* (login, registro, refresh, recuperação de senha) nunca entram nesse ciclo
//    (as telas tratam os próprios 401); as de sessão (me, confirm-email, resend-email-verification) entram.
import type { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from 'axios';

export interface TokenPair {
  readonly accessToken: string;
  readonly refreshToken: string;
}

export interface RefreshResult {
  readonly accessToken: string;
  /** O contrato do backend permite nulo; nesse caso o refresh token atual é mantido. */
  readonly refreshToken?: string | null;
}

export interface AuthRefreshDeps {
  /** Tokens atuais da sessão (null quando não há sessão). */
  getTokens(): { accessToken: string | null; refreshToken: string | null };
  /** Chama POST /api/v1/auth/refresh e devolve o novo par. Deve rejeitar com o erro do axios. */
  requestRefresh(refreshToken: string): Promise<RefreshResult>;
  /** Persiste o novo par preservando o restante da sessão (userId, coupleId). */
  saveTokens(tokens: TokenPair): Promise<void>;
  /** Limpa a sessão, avisa o usuário e leva ao login. */
  onSessionExpired(): Promise<void> | void;
  /**
   * Época da sessão: muda quando alguém entra ou sai da conta e quando o grupo ativo muda. Uma requisição lembra
   * a época em que saiu; se o 401 chega numa época diferente, ela é de outra sessão (ou de outro grupo) e não é
   * repetida com o token de quem está agora.
   */
  getSessionEpoch?(): number;
}

const AUTH_ROUTE_PREFIX = '/api/v1/auth';

// Rotas de /auth que exigem sessão e portanto passam pelo ciclo de refresh (um access token vencido não pode
// derrubar a confirmação de e-mail). As demais (login, registro, refresh, recuperação de senha...) são anônimas.
const SIGNED_IN_AUTH_ROUTES = [
  '/api/v1/auth/me',
  '/api/v1/auth/confirm-email',
  '/api/v1/auth/resend-email-verification',
];

/** Só o caminho da requisição (sem origem, query string, fragmento nem barra final), para comparar caminhos exatos. */
function pathOf(url: string): string {
  return url
    .replace(/^[a-z][a-z0-9+.-]*:\/\/[^/]+/i, '')
    .split(/[?#]/)[0]
    .replace(/\/+$/, '');
}

function isAnonymousAuthRoute(url: string): boolean {
  if (!url.includes(AUTH_ROUTE_PREFIX)) return false;
  return !SIGNED_IN_AUTH_ROUTES.includes(pathOf(url));
}

type RetriableConfig = InternalAxiosRequestConfig & { _retriedAfterRefresh?: boolean; _sessionEpoch?: number };

/** Resultado de uma tentativa de refresh compartilhada entre as requisições em espera. */
type RefreshOutcome =
  | { readonly kind: 'renewed'; readonly accessToken: string }
  /** O servidor recusou o refresh token: a sessão acabou. */
  | { readonly kind: 'rejected' }
  /** Falha de rede/servidor: não dá para saber se a sessão ainda vale. */
  | { readonly kind: 'unavailable'; readonly error: unknown };

function bearer(token: string): string {
  return `Bearer ${token}`;
}

/** 400/401/403 do endpoint de refresh significam token inválido, expirado ou já rotacionado. */
function isRefreshRejection(error: unknown): boolean {
  const status = (error as AxiosError | undefined)?.response?.status;
  return status === 400 || status === 401 || status === 403;
}

export function installAuthRefresh(instance: AxiosInstance, deps: AuthRefreshDeps): void {
  let refreshInFlight: Promise<RefreshOutcome> | null = null;
  let expirationInFlight: Promise<void> | null = null;

  /** Encerra a sessão uma única vez, mesmo com vários 401 chegando juntos. */
  function expireSession(): Promise<void> {
    if (!expirationInFlight) {
      expirationInFlight = Promise.resolve()
        .then(() => deps.onSessionExpired())
        .finally(() => {
          expirationInFlight = null;
        });
    }
    return expirationInFlight;
  }

  const currentEpoch = () => deps.getSessionEpoch?.();

  async function runRefresh(refreshToken: string): Promise<RefreshOutcome> {
    const epochAtStart = currentEpoch();
    // O usuário saiu (ou entrou outro) enquanto o refresh estava em andamento: o resultado, positivo ou
    // negativo, não vale para ninguém. Não grava tokens, não repete a requisição e não avisa "sessão expirada".
    const sessionChanged = () =>
      deps.getTokens().refreshToken !== refreshToken || currentEpoch() !== epochAtStart;

    let result: RefreshResult;
    try {
      result = await deps.requestRefresh(refreshToken);
    } catch (error) {
      if (isRefreshRejection(error)) {
        // Recusa para uma sessão que já acabou não pode derrubar quem entrou depois.
        if (sessionChanged()) return { kind: 'rejected' };
        await expireSession();
        return { kind: 'rejected' };
      }
      return { kind: 'unavailable', error };
    }

    if (sessionChanged()) {
      return { kind: 'rejected' };
    }

    if (!result?.accessToken) {
      await expireSession();
      return { kind: 'rejected' };
    }

    await deps.saveTokens({
      accessToken: result.accessToken,
      refreshToken: result.refreshToken || refreshToken,
    });
    // O logout pode ter chegado durante a gravação (a sessão guarda isso e descarta os tokens): não repete.
    if (currentEpoch() !== epochAtStart) {
      return { kind: 'rejected' };
    }
    return { kind: 'renewed', accessToken: result.accessToken };
  }

  /** Devolve o refresh em andamento ou inicia um; nunca há dois em paralelo. */
  function refreshOnce(refreshToken: string): Promise<RefreshOutcome> {
    if (!refreshInFlight) {
      refreshInFlight = runRefresh(refreshToken).finally(() => {
        refreshInFlight = null;
      });
    }
    return refreshInFlight;
  }

  function retry(config: RetriableConfig, accessToken: string) {
    config._retriedAfterRefresh = true;
    config.headers.set('Authorization', bearer(accessToken));
    return instance.request(config);
  }

  instance.interceptors.request.use((config) => {
    const { accessToken } = deps.getTokens();
    if (accessToken) {
      config.headers.set('Authorization', bearer(accessToken));
    }
    (config as RetriableConfig)._sessionEpoch = currentEpoch();
    return config;
  });

  instance.interceptors.response.use(
    (response) => response,
    async (error: AxiosError) => {
      const config = error?.config as RetriableConfig | undefined;
      if (error?.response?.status !== 401 || !config) {
        return Promise.reject(error);
      }
      if (isAnonymousAuthRoute(config.url ?? '')) {
        return Promise.reject(error); // login/registro/refresh/recuperação tratam os próprios 401
      }

      // 401 atrasado de uma requisição feita por OUTRA sessão (o usuário saiu e outro entrou): não há o que
      // renovar nem repetir com o token de quem está agora, e a sessão atual não tem culpa.
      if (config._sessionEpoch !== undefined && config._sessionEpoch !== currentEpoch()) {
        return Promise.reject(error);
      }

      // A repetição com o token novo também foi recusada: não há novo ciclo.
      if (config._retriedAfterRefresh) {
        await expireSession();
        return Promise.reject(error);
      }

      // Outro 401 pode já ter concluído o refresh enquanto esta requisição estava em trânsito
      // com o token antigo. Nesse caso basta repetir com o token vigente.
      if (!refreshInFlight) {
        const current = deps.getTokens().accessToken;
        const sentWith = config.headers?.get?.('Authorization');
        if (current && typeof sentWith === 'string' && sentWith !== bearer(current)) {
          return retry(config, current);
        }
      }

      const { accessToken, refreshToken } = deps.getTokens();
      if (!refreshInFlight && !refreshToken) {
        // Sem nenhum token a sessão já foi encerrada (ex.: 401 atrasado de uma requisição
        // feita antes do logout); não há o que limpar nem por que avisar de novo.
        if (accessToken) {
          await expireSession();
        }
        return Promise.reject(error);
      }

      const outcome = await (refreshInFlight ?? refreshOnce(refreshToken as string));
      if (outcome.kind === 'renewed') {
        return retry(config, outcome.accessToken);
      }
      if (outcome.kind === 'unavailable') {
        // Sem resposta do servidor: mantém a sessão e devolve o erro de rede a quem chamou.
        return Promise.reject(outcome.error);
      }
      return Promise.reject(error);
    },
  );
}
