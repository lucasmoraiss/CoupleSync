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

import { useSessionStore } from '../sessionStore';

const persisted = () => JSON.parse(secureStore['couplesync_session']);

beforeEach(async () => {
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', null);
});

describe('setAccessTokenAndCouple (criar/entrar no grupo)', () => {
  it('guarda o refresh token novo quando a API devolve um (usuário removido de um grupo)', async () => {
    await useSessionStore.getState().setAccessTokenAndCouple('access-2', 'couple-9', 'refresh-2');

    expect(useSessionStore.getState().refreshToken).toBe('refresh-2');
    expect(persisted()).toMatchObject({ accessToken: 'access-2', refreshToken: 'refresh-2', coupleId: 'couple-9' });
  });

  it.each([[undefined], [null], ['']])('mantém o refresh token atual quando a API devolve %p', async (value) => {
    await useSessionStore.getState().setAccessTokenAndCouple('access-2', 'couple-9', value as string | null | undefined);

    expect(useSessionStore.getState().refreshToken).toBe('refresh-1');
    expect(persisted()).toMatchObject({ accessToken: 'access-2', refreshToken: 'refresh-1', coupleId: 'couple-9' });
  });
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
