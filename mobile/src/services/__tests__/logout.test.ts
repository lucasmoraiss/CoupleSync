// Sair da conta: tokens, cache de consultas, estados em memória e fila de notificações somem,
// com ou sem resposta do servidor.
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

const mockServerLogout = jest.fn();
jest.mock('@/services/apiClient', () => ({
  authApiClient: { logout: (...args: unknown[]) => mockServerLogout(...args) },
}));

import { QueryClient } from '@tanstack/react-query';
import { logout } from '../logout';
import { useSessionStore } from '@/state/sessionStore';
import { useDashboardStore } from '@/state/dashboardStore';
import { clearUserData, registerUserDataCleaner } from '@/state/userData';

// Mesma regra de registro que o app usa em services/queryClient.ts (sem puxar o módulo real).
const queryClient = new QueryClient();
registerUserDataCleaner(() => {
  queryClient.clear();
});

async function signIn() {
  await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', 'couple-1');
  queryClient.setQueryData(['transactions'], [{ id: 'a-private-transaction' }]);
  queryClient.setQueryData(['goals'], [{ id: 'a-private-goal' }]);
  useDashboardStore.getState().setDateRange('2026-01-01', '2026-01-31');
}

beforeEach(async () => {
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockServerLogout.mockReset();
  await signIn();
});

describe('logout', () => {
  it('apaga tokens, cache de consultas e estados em memória', async () => {
    mockServerLogout.mockResolvedValue({ status: 204 });

    await logout();

    const session = useSessionStore.getState();
    expect(session.accessToken).toBeNull();
    expect(session.refreshToken).toBeNull();
    expect(session.userId).toBeNull();
    expect(session.coupleId).toBeNull();
    expect(secureStore['couplesync_session']).toBeUndefined();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(useDashboardStore.getState().startDate).toBe('');
    expect(useDashboardStore.getState().endDate).toBe('');
  });

  it('avisa o servidor com o refresh token que havia antes de apagar', async () => {
    mockServerLogout.mockResolvedValue({ status: 204 });

    await logout();

    expect(mockServerLogout).toHaveBeenCalledTimes(1);
    expect(mockServerLogout).toHaveBeenCalledWith('refresh-1');
  });

  it('sai do mesmo jeito quando o servidor não responde', async () => {
    mockServerLogout.mockRejectedValue(Object.assign(new Error('Network Error'), { code: 'ERR_NETWORK' }));

    await expect(logout()).resolves.toBeUndefined();

    expect(useSessionStore.getState().accessToken).toBeNull();
    expect(secureStore['couplesync_session']).toBeUndefined();
    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
  });

  it('já não há nada no aparelho quando a chamada ao servidor acontece', async () => {
    let storedWhenServerCalled: string | undefined = 'not-called';
    mockServerLogout.mockImplementation(async () => {
      storedWhenServerCalled = secureStore['couplesync_session'];
      return { status: 204 };
    });

    await logout();

    expect(storedWhenServerCalled).toBeUndefined();
  });

  it('sem sessão não chama o servidor', async () => {
    await clearUserData();
    mockServerLogout.mockReset();

    await logout();

    expect(mockServerLogout).not.toHaveBeenCalled();
  });

  it('um limpador que falha não impede os outros nem a limpeza da sessão', async () => {
    const unregistered = jest.fn();
    registerUserDataCleaner(() => {
      throw new Error('boom');
    });
    registerUserDataCleaner(unregistered);
    mockServerLogout.mockResolvedValue({ status: 204 });

    await logout();

    expect(unregistered).toHaveBeenCalled();
    expect(useSessionStore.getState().accessToken).toBeNull();
    expect(secureStore['couplesync_session']).toBeUndefined();
  });
});
