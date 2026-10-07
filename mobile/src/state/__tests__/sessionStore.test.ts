const secureStore: Record<string, string> = {};

jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn(async (key: string) => secureStore[key] ?? null),
  deleteItemAsync: jest.fn(async (key: string) => {
    delete secureStore[key];
  }),
}));

import * as SecureStore from 'expo-secure-store';
import { getSessionEpoch, useSessionStore } from '../sessionStore';

const persisted = () => JSON.parse(secureStore['couplesync_session']);

beforeEach(async () => {
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', null);
});

describe('clearCouple (grupo guardado que o servidor não reconhece)', () => {
  it('esquece o grupo e mantém a sessão', async () => {
    await useSessionStore.getState().setCoupleId('couple-1');

    await useSessionStore.getState().clearCouple();

    const state = useSessionStore.getState();
    expect(state.coupleId).toBeNull();
    expect(state.accessToken).toBe('access-1');
    expect(state.refreshToken).toBe('refresh-1');
    expect(persisted()).toMatchObject({ coupleId: null, accessToken: 'access-1', refreshToken: 'refresh-1', userId: 'user-1' });
  });

  it('não recria uma sessão que já foi encerrada', async () => {
    await useSessionStore.getState().clearSession();

    await useSessionStore.getState().clearCouple();

    expect(secureStore['couplesync_session']).toBeUndefined();
    expect(useSessionStore.getState().userId).toBeNull();
  });
});

/** Faz a PRÓXIMA gravação no armazenamento seguro demorar até `release()` ser chamado (a gravação acontece ao liberar). */
function holdNextWrite() {
  let release!: () => void;
  const gate = new Promise<void>((resolve) => {
    release = resolve;
  });
  (SecureStore.setItemAsync as jest.Mock).mockImplementationOnce(async (key: string, value: string) => {
    await gate;
    secureStore[key] = value;
  });
  return release;
}

describe('logout durante a gravação no armazenamento seguro', () => {
  it.each([
    ['setTokens', () => useSessionStore.getState().setTokens('access-2', 'refresh-2')],
    ['setActiveGroup', () => useSessionStore.getState().setActiveGroup('access-2', 'couple-9', 'refresh-2')],
    ['setCoupleId', () => useSessionStore.getState().setCoupleId('couple-9')],
  ])('%s: os tokens de quem saiu não voltam, nem na memória nem no armazenamento', async (_name, action) => {
    const release = holdNextWrite();
    const pending = action();

    await useSessionStore.getState().clearSession(); // logout chega no meio da gravação
    release();
    await pending;

    const state = useSessionStore.getState();
    expect(state.accessToken).toBeNull();
    expect(state.refreshToken).toBeNull();
    expect(state.userId).toBeNull();
    expect(secureStore['couplesync_session']).toBeUndefined();
  });

  it('setSession: um logout durante a gravação do login não deixa a sessão voltar', async () => {
    const release = holdNextWrite();
    const pending = useSessionStore.getState().setSession('access-9', 'refresh-9', 'user-9', null);

    await useSessionStore.getState().clearSession();
    release();
    await pending;

    expect(useSessionStore.getState().userId).toBeNull();
    expect(secureStore['couplesync_session']).toBeUndefined();
  });

  it('se outro usuário entrou durante a espera, o armazenamento fica com a sessão dele', async () => {
    const release = holdNextWrite();
    const pending = useSessionStore.getState().setTokens('access-2', 'refresh-2'); // do usuário 1

    await useSessionStore.getState().clearSession();
    await useSessionStore.getState().setSession('access-B', 'refresh-B', 'user-B', null);
    release();
    await pending;

    expect(useSessionStore.getState()).toMatchObject({ userId: 'user-B', accessToken: 'access-B', refreshToken: 'refresh-B' });
    expect(persisted()).toMatchObject({ userId: 'user-B', accessToken: 'access-B', refreshToken: 'refresh-B' });
  });

  it('sem logout a gravação vale normalmente', async () => {
    await useSessionStore.getState().setTokens('access-2', 'refresh-2');
    expect(useSessionStore.getState().accessToken).toBe('access-2');
    expect(persisted()).toMatchObject({ accessToken: 'access-2', refreshToken: 'refresh-2' });
  });
});

describe('época da sessão', () => {
  it('sobe no login e no logout, não na renovação de tokens', async () => {
    const start = getSessionEpoch();
    await useSessionStore.getState().setTokens('a', 'r');
    expect(getSessionEpoch()).toBe(start);
    await useSessionStore.getState().clearSession();
    expect(getSessionEpoch()).toBeGreaterThan(start);
    const afterLogout = getSessionEpoch();
    await useSessionStore.getState().setSession('a', 'r', 'u', null);
    expect(getSessionEpoch()).toBeGreaterThan(afterLogout);
  });
});
