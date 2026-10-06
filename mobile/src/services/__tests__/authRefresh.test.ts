import axios, { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from 'axios';
import { installAuthRefresh, RefreshResult, TokenPair } from '../authRefresh';

interface Call {
  readonly url: string;
  readonly authorization: string | null;
}

interface HarnessOptions {
  /** Token que o servidor falso passa a aceitar depois do refresh. Padrão: o token devolvido pelo refresh. */
  readonly acceptAfterRefresh?: string;
  /** Sessão inicial guardada no aparelho. */
  readonly session?: { accessToken: string | null; refreshToken: string | null };
  /** O que o refresh devolve (padrão: par novo). */
  readonly refreshResult?: RefreshResult;
}

function httpError(config: InternalAxiosRequestConfig, status: number): AxiosError {
  return new AxiosError(
    `Request failed with status code ${status}`,
    AxiosError.ERR_BAD_REQUEST,
    config,
    null,
    { status, statusText: String(status), data: { code: 'UNAUTHORIZED' }, headers: {}, config },
  );
}

/**
 * Servidor falso: aceita só o access token vigente e faz rotação do refresh token
 * (um refresh token só vale uma vez, como no backend).
 */
function createHarness(options: HarnessOptions = {}) {
  const session = {
    accessToken: 'access-1' as string | null,
    refreshToken: 'refresh-1' as string | null,
    userId: 'user-1',
    coupleId: 'couple-1',
    ...options.session,
  };
  const server = { acceptedAccessToken: 'expirado-no-servidor', validRefreshToken: 'refresh-1' };
  const calls: Call[] = [];
  let releaseRefresh: (() => void) | null = null;
  let holdRefresh = false;

  const instance: AxiosInstance = axios.create({
    baseURL: 'http://api.test',
    adapter: async (config) => {
      const authorization = (config.headers.get('Authorization') as string | undefined) ?? null;
      calls.push({ url: config.url ?? '', authorization });
      // Deixa o event loop girar para que requisições "simultâneas" se sobreponham de fato
      await new Promise((resolve) => setImmediate(resolve));
      if (authorization === `Bearer ${server.acceptedAccessToken}`) {
        return { status: 200, statusText: 'OK', data: { ok: true }, headers: {}, config };
      }
      throw httpError(config, 401);
    },
  });

  const requestRefresh = jest.fn(async (refreshToken: string): Promise<RefreshResult> => {
    if (holdRefresh) {
      await new Promise<void>((resolve) => {
        releaseRefresh = resolve;
      });
    }
    if (refreshToken !== server.validRefreshToken) {
      // Token já rotacionado/invalidado: o backend responde 401
      throw httpError({ url: '/api/v1/auth/refresh' } as InternalAxiosRequestConfig, 401);
    }
    const result = options.refreshResult ?? { accessToken: 'access-2', refreshToken: 'refresh-2' };
    server.validRefreshToken = result.refreshToken ?? server.validRefreshToken;
    server.acceptedAccessToken = options.acceptAfterRefresh ?? result.accessToken;
    return result;
  });

  const saveTokens = jest.fn(async (tokens: TokenPair) => {
    session.accessToken = tokens.accessToken;
    session.refreshToken = tokens.refreshToken;
  });

  const onSessionExpired = jest.fn(async () => {
    session.accessToken = null;
    session.refreshToken = null;
  });

  installAuthRefresh(instance, {
    getTokens: () => ({ accessToken: session.accessToken, refreshToken: session.refreshToken }),
    requestRefresh,
    saveTokens,
    onSessionExpired,
  });

  return {
    instance,
    session,
    server,
    calls,
    requestRefresh,
    saveTokens,
    onSessionExpired,
    holdRefresh: () => {
      holdRefresh = true;
    },
    releaseRefresh: () => {
      holdRefresh = false;
      releaseRefresh?.();
    },
  };
}

async function statusOf(promise: Promise<unknown>): Promise<number | 'ok' | 'sem-resposta'> {
  try {
    await promise;
    return 'ok';
  } catch (err) {
    return (err as AxiosError).response?.status ?? 'sem-resposta';
  }
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

describe('A03 — renovação de sessão no 401', () => {
  it('um 401 dispara um refresh e a requisição é repetida com o token novo', async () => {
    const h = createHarness();

    const response = await h.instance.get('/api/v1/dashboard');

    expect(response.status).toBe(200);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);
    expect(h.requestRefresh).toHaveBeenCalledWith('refresh-1');
    expect(h.calls).toEqual([
      { url: '/api/v1/dashboard', authorization: 'Bearer access-1' },
      { url: '/api/v1/dashboard', authorization: 'Bearer access-2' },
    ]);
    expect(h.onSessionExpired).not.toHaveBeenCalled();
  });

  it('grava o novo par de tokens preservando userId e coupleId', async () => {
    const h = createHarness();

    await h.instance.get('/api/v1/dashboard');

    expect(h.saveTokens).toHaveBeenCalledWith({ accessToken: 'access-2', refreshToken: 'refresh-2' });
    expect(h.session).toEqual({
      accessToken: 'access-2',
      refreshToken: 'refresh-2',
      userId: 'user-1',
      coupleId: 'couple-1',
    });
  });

  it('três 401 simultâneos esperam exatamente um refresh', async () => {
    const h = createHarness();
    h.holdRefresh();

    const pending = [
      h.instance.get('/api/v1/dashboard'),
      h.instance.get('/api/v1/transactions'),
      h.instance.get('/api/v1/goals'),
    ].map((p) => p.then((r) => r.status, (e: AxiosError) => e.response?.status));
    // Espera os três 401 chegarem enquanto o refresh ainda está em andamento
    for (let i = 0; i < 5; i += 1) await flush();
    expect(h.calls).toHaveLength(3);
    h.releaseRefresh();

    const statuses = await Promise.all(pending);

    expect(statuses).toEqual([200, 200, 200]);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);
    expect(h.calls.slice(3).map((c) => c.authorization)).toEqual([
      'Bearer access-2',
      'Bearer access-2',
      'Bearer access-2',
    ]);
    expect(h.onSessionExpired).not.toHaveBeenCalled();
  });

  it('401 atrasado de um token já renovado não dispara um segundo refresh', async () => {
    const h = createHarness();
    await h.instance.get('/api/v1/dashboard'); // renova: access-2 em vigor

    // Requisição que saiu com o token antigo e só agora recebeu o 401
    let firstAttempt = true;
    const response = await h.instance.get('/api/v1/goals', {
      transformRequest: [
        (data, headers) => {
          if (firstAttempt) headers.set('Authorization', 'Bearer access-1');
          firstAttempt = false;
          return data;
        },
      ],
    });

    expect(response.status).toBe(200);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);
    expect(h.onSessionExpired).not.toHaveBeenCalled();
  });

  it('refresh recusado pelo servidor limpa a sessão uma vez, sem laço', async () => {
    const h = createHarness({ session: { accessToken: 'access-1', refreshToken: 'refresh-revogado' } });

    const status = await statusOf(h.instance.get('/api/v1/dashboard'));

    expect(status).toBe(401);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);
    expect(h.onSessionExpired).toHaveBeenCalledTimes(1);
    expect(h.saveTokens).not.toHaveBeenCalled();
    expect(h.calls).toHaveLength(1); // a requisição original não é repetida
  });

  it('refresh recusado com três requisições simultâneas avisa sessão expirada uma única vez', async () => {
    const h = createHarness({ session: { accessToken: 'access-1', refreshToken: 'refresh-revogado' } });

    const statuses = await Promise.all([
      statusOf(h.instance.get('/api/v1/dashboard')),
      statusOf(h.instance.get('/api/v1/transactions')),
      statusOf(h.instance.get('/api/v1/goals')),
    ]);

    expect(statuses).toEqual([401, 401, 401]);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);
    expect(h.onSessionExpired).toHaveBeenCalledTimes(1);
    expect(h.calls).toHaveLength(3);
  });

  it('se a requisição repetida também devolve 401, encerra a sessão sem novo ciclo de refresh', async () => {
    const h = createHarness({ acceptAfterRefresh: 'token-que-o-servidor-nunca-emite' });

    const status = await statusOf(h.instance.get('/api/v1/dashboard'));

    expect(status).toBe(401);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);
    expect(h.calls).toHaveLength(2); // original + uma repetição, nada além disso
    expect(h.onSessionExpired).toHaveBeenCalledTimes(1);
  });

  it('401 nas rotas /api/v1/auth/* não dispara refresh nem encerra a sessão', async () => {
    const h = createHarness();

    const login = await statusOf(h.instance.post('/api/v1/auth/login', { email: 'a@b.c', password: 'x' }));
    const refresh = await statusOf(h.instance.post('/api/v1/auth/refresh', { refreshToken: 'x' }));

    expect([login, refresh]).toEqual([401, 401]);
    expect(h.requestRefresh).not.toHaveBeenCalled();
    expect(h.onSessionExpired).not.toHaveBeenCalled();
    expect(h.calls).toHaveLength(2);
  });

  it('as rotas de /auth que exigem sessão (me, confirm-email) renovam o token como qualquer outra', async () => {
    const h = createHarness();

    const me = await statusOf(h.instance.get('/api/v1/auth/me'));
    const confirm = await statusOf(h.instance.post('/api/v1/auth/confirm-email', { code: '123456' }));

    expect([me, confirm]).toEqual(['ok', 'ok']);
    expect(h.requestRefresh).toHaveBeenCalledTimes(1); // o segundo já saiu com o token renovado
    expect(h.onSessionExpired).not.toHaveBeenCalled();
  });

  it('sem refresh token guardado, encerra a sessão sem chamar o refresh', async () => {
    const h = createHarness({ session: { accessToken: 'access-1', refreshToken: null } });

    const status = await statusOf(h.instance.get('/api/v1/dashboard'));

    expect(status).toBe(401);
    expect(h.requestRefresh).not.toHaveBeenCalled();
    expect(h.onSessionExpired).toHaveBeenCalledTimes(1);
  });

  it('falha de rede durante o refresh não apaga a sessão', async () => {
    const h = createHarness();
    h.requestRefresh.mockRejectedValueOnce(new AxiosError('Network Error', AxiosError.ERR_NETWORK));

    const status = await statusOf(h.instance.get('/api/v1/dashboard'));

    expect(status).toBe('sem-resposta');
    expect(h.onSessionExpired).not.toHaveBeenCalled();
    expect(h.session.refreshToken).toBe('refresh-1');
    expect(h.calls).toHaveLength(1);
  });

  it('refresh que termina depois do logout não grava tokens, não repete a requisição nem avisa sessão expirada', async () => {
    const h = createHarness();
    h.holdRefresh();

    const pending = statusOf(h.instance.get('/api/v1/dashboard'));
    await flush();
    await flush();
    expect(h.requestRefresh).toHaveBeenCalledTimes(1);

    // O usuário sai enquanto o refresh está em andamento.
    h.session.accessToken = null;
    h.session.refreshToken = null;
    h.releaseRefresh();

    expect(await pending).toBe(401);
    expect(h.saveTokens).not.toHaveBeenCalled();
    expect(h.onSessionExpired).not.toHaveBeenCalled();
    expect(h.calls).toHaveLength(1); // nenhuma repetição com o token novo
  });

  it('quando o refresh não devolve refresh token, mantém o anterior', async () => {
    const h = createHarness({ refreshResult: { accessToken: 'access-2', refreshToken: null } });

    const response = await h.instance.get('/api/v1/dashboard');

    expect(response.status).toBe(200);
    expect(h.saveTokens).toHaveBeenCalledWith({ accessToken: 'access-2', refreshToken: 'refresh-1' });
  });

  it('erros que não são 401 passam direto', async () => {
    const h = createHarness();
    h.instance.defaults.adapter = async (config) => {
      throw httpError(config, 403);
    };

    const status = await statusOf(h.instance.get('/api/v1/dashboard'));

    expect(status).toBe(403);
    expect(h.requestRefresh).not.toHaveBeenCalled();
    expect(h.onSessionExpired).not.toHaveBeenCalled();
  });
});
