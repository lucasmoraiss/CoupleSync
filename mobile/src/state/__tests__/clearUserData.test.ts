// Sair da conta com telas montadas: a limpeza não pode deixar consulta nenhuma ser refeita com o token
// do usuário que está saindo, nem gravar no cache uma resposta que chega depois.
const secureStore: Record<string, string> = {};
let failDelete = false;

jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn(async (key: string) => secureStore[key] ?? null),
  deleteItemAsync: jest.fn(async (key: string) => {
    if (failDelete) throw new Error('keystore indisponível');
    delete secureStore[key];
  }),
}));

import { QueryClient, QueryObserver } from '@tanstack/react-query';
import { useSessionStore } from '../sessionStore';
import { useDashboardStore } from '../dashboardStore';
import { clearUserData, registerUserDataCleaner, resetUserCaches } from '../userData';
import { clearGroupScopedQueries, createAppQueryClient } from '@/services/queryClient';

const queryClient: QueryClient = createAppQueryClient();
registerUserDataCleaner(async () => {
  await queryClient.cancelQueries();
  queryClient.clear();
});

const tick = () => new Promise((resolve) => setImmediate(resolve));

beforeEach(async () => {
  failDelete = false;
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', 'couple-1');
});

describe('clearUserData com uma tela montada', () => {
  it('nenhuma consulta é refeita com o token de quem saiu, e nada fica no cache', async () => {
    const tokensSeenByFetches: Array<string | null> = [];
    let releaseFirst: (() => void) | undefined;
    const queryFn = jest.fn(async () => {
      tokensSeenByFetches.push(useSessionStore.getState().accessToken);
      if (queryFn.mock.calls.length === 1) {
        await new Promise<void>((resolve) => {
          releaseFirst = resolve;
        });
      }
      return { members: ['Ana'] };
    });

    // A "tela": observa a consulta e, como o DashboardScreen, refaz as opções quando o store do painel muda.
    const keyFor = () => ['dashboard', useDashboardStore.getState().startDate, useDashboardStore.getState().endDate];
    useDashboardStore.getState().setDateRange('2026-01-01', '2026-01-31');
    const observer = new QueryObserver(queryClient, { queryKey: keyFor(), queryFn });
    const unsubscribe = observer.subscribe(() => {});
    const unsubscribeStore = useDashboardStore.subscribe(() => observer.setOptions({ queryKey: keyFor(), queryFn }));
    await tick();
    expect(queryFn).toHaveBeenCalledTimes(1); // busca em andamento, com o token vivo

    await clearUserData();
    releaseFirst?.();
    await tick();
    await tick();

    unsubscribe();
    unsubscribeStore();
    // Só a busca que já estava em andamento viu o token; as que a tela refez ao reagir à limpeza já não têm token.
    expect(tokensSeenByFetches.filter((t) => t !== null)).toHaveLength(1);
    expect(queryClient.getQueryCache().getAll().filter((q) => q.state.data !== undefined)).toHaveLength(0);
  });

  it('a sessão em memória já está vazia quando os limpadores rodam', async () => {
    let accessTokenSeenByCleaner: string | null | undefined;
    registerUserDataCleaner(() => {
      accessTokenSeenByCleaner = useSessionStore.getState().accessToken;
    });

    await clearUserData();

    expect(accessTokenSeenByCleaner).toBeNull();
  });

  it('termina deslogado mesmo se o armazenamento seguro falhar ao apagar', async () => {
    failDelete = true;

    await expect(clearUserData()).resolves.toBeUndefined();

    expect(useSessionStore.getState().accessToken).toBeNull();
    expect(useSessionStore.getState().refreshToken).toBeNull();
    // O que sobrou no armazenamento não reabre a sessão na próxima abertura do app (keystore já recuperado).
    failDelete = false;
    await useSessionStore.getState().hydrateFromStore();
    expect(useSessionStore.getState().accessToken).toBeNull();
  });

  it('resetUserCaches (usado no login) esvazia os limpadores sem tocar na sessão', async () => {
    queryClient.setQueryData(['transactions'], [{ id: 'x' }]);

    await resetUserCaches();

    expect(queryClient.getQueryCache().getAll()).toHaveLength(0);
    expect(useSessionStore.getState().accessToken).toBe('access-1');
  });
});

describe('clearGroupScopedQueries', () => {
  it('remove o cache do grupo antigo e mantém só a consulta do próprio grupo', () => {
    const client = createAppQueryClient();
    client.setQueryData(['couple-me'], { coupleId: 'c' });
    client.setQueryData(['transactions'], []);
    client.setQueryData(['goals', 1], []);

    clearGroupScopedQueries(client);

    expect(client.getQueryCache().getAll().map((q) => q.queryKey[0])).toEqual(['couple-me']);
  });
});
