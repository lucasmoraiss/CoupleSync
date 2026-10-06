// Troca de grupo ativo com o app aberto: nada do grupo anterior pode aparecer no grupo novo, nada capturado para
// o grupo anterior pode ser lançado no novo, e as telas buscam tudo de novo com o token novo.
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

const mockPost = jest.fn();
jest.mock('@/services/apiClient', () => ({
  __esModule: true,
  default: { post: (...args: unknown[]) => mockPost(...args) },
}));

import { QueryObserver } from '@tanstack/react-query';
import { activateGroup, type ActivateGroupDeps } from '../activateGroup';
import { groupSessionDeps } from '../groupSession';
import { createAppQueryClient } from '@/services/queryClient';
import { getSessionEpoch, useSessionStore } from '@/state/sessionStore';
import { useDashboardStore } from '@/state/dashboardStore';
import { useGroupEpoch } from '@/state/groupEpoch';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { getPendingRetryCount, handleRawNotificationEvent } from '@/modules/integrations/notification-capture/eventUploader';

const tick = () => new Promise((resolve) => setImmediate(resolve));

beforeEach(async () => {
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockPost.mockReset();
  await useSessionStore.getState().setSession('access-A', 'refresh-A', 'user-1', 'group-A');
});

describe('activateGroup (ordem)', () => {
  it('descarta capturas pendentes, troca a sessão, só então cancela e limpa, e remonta por último', async () => {
    const steps: string[] = [];
    const deps: ActivateGroupDeps = {
      dropPendingCaptures: () => void steps.push('drop'),
      saveSession: async () => void steps.push('session'),
      cancelQueries: async () => void steps.push('cancel'),
      clearGroupData: () => void steps.push('clear'),
      remountScreens: () => void steps.push('remount'),
    };

    await activateGroup({ accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B' }, deps);

    expect(steps).toEqual(['drop', 'session', 'cancel', 'clear', 'remount']);
  });

  it('se a sessão não pôde ser gravada, nada é limpo nem remontado (o app continua no grupo anterior)', async () => {
    const steps: string[] = [];
    const deps: ActivateGroupDeps = {
      dropPendingCaptures: () => void steps.push('drop'),
      saveSession: async () => {
        throw new Error('keystore indisponível');
      },
      cancelQueries: async () => void steps.push('cancel'),
      clearGroupData: () => void steps.push('clear'),
      remountScreens: () => void steps.push('remount'),
    };

    await expect(activateGroup({ accessToken: 'x', coupleId: 'group-B' }, deps)).rejects.toThrow('keystore');

    expect(steps).toEqual(['drop']);
  });
});

describe('ficar sem grupo ativo (saiu do último grupo)', () => {
  it('limpa tudo mas NÃO remonta as telas: sem grupo não há o que buscar, e a busca só mostraria um aviso de erro', async () => {
    const steps: string[] = [];
    const deps: ActivateGroupDeps = {
      dropPendingCaptures: () => void steps.push('drop'),
      saveSession: async () => void steps.push('session'),
      cancelQueries: async () => void steps.push('cancel'),
      clearGroupData: () => void steps.push('clear'),
      remountScreens: () => void steps.push('remount'),
    };

    await activateGroup({ accessToken: 'access-0', refreshToken: 'refresh-0', coupleId: null }, deps);

    expect(steps).toEqual(['drop', 'session', 'cancel', 'clear']);
  });
});

describe('troca de grupo com as peças reais do app', () => {
  it('uma tela montada passa a mostrar só o grupo novo, buscado com o token novo', async () => {
    const client = createAppQueryClient();
    const fetches: Array<{ token: string | null; group: string | null }> = [];
    let releaseFirst: (() => void) | undefined;
    const queryFn = jest.fn(async () => {
      const { accessToken, coupleId } = useSessionStore.getState();
      fetches.push({ token: accessToken, group: coupleId });
      if (queryFn.mock.calls.length === 2) {
        // Segunda busca do grupo A: fica em andamento durante a troca.
        await new Promise<void>((resolve) => {
          releaseFirst = resolve;
        });
      }
      return { ofGroup: coupleId };
    });

    // A "tela": observa a consulta e é remontada quando a época do grupo muda (como o layout principal faz).
    let observer = new QueryObserver(client, { queryKey: ['transactions'], queryFn, staleTime: 30_000 });
    let unsubscribe = observer.subscribe(() => {});
    const unsubscribeEpoch = useGroupEpoch.subscribe(() => {
      unsubscribe();
      observer = new QueryObserver(client, { queryKey: ['transactions'], queryFn, staleTime: 30_000 });
      unsubscribe = observer.subscribe(() => {});
    });
    client.setQueryData(['couple-me'], { coupleId: 'group-A' });
    client.setQueryData(['my-groups'], { activeCoupleId: 'group-A' });
    useDashboardStore.getState().setDateRange('2026-01-01', '2026-01-31');
    await tick();
    expect(observer.getCurrentResult().data).toEqual({ ofGroup: 'group-A' });
    void observer.refetch(); // em andamento, com o token do grupo A
    await tick();
    const epochBefore = getSessionEpoch();

    await activateGroup(
      { accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B' },
      groupSessionDeps(client),
    );
    releaseFirst?.(); // a resposta do grupo A chega depois da troca
    await tick();
    await tick();

    unsubscribe();
    unsubscribeEpoch();
    // A tela remontada buscou de novo, já pelo grupo B; a resposta atrasada do grupo A não entrou no cache.
    expect(observer.getCurrentResult().data).toEqual({ ofGroup: 'group-B' });
    expect(fetches[fetches.length - 1]).toEqual({ token: 'access-B', group: 'group-B' });
    expect(client.getQueryData(['transactions'])).toEqual({ ofGroup: 'group-B' });
    // A consulta do próprio grupo também era do grupo anterior; a lista de grupos é refeita.
    expect(client.getQueryData(['couple-me'])).toBeUndefined();
    expect(client.getQueryData(['my-groups'])).toBeUndefined();
    expect(useDashboardStore.getState().startDate).toBe('');
    // Sessão do grupo novo, gravada; requisições enviadas antes da troca não são repetidas com o token novo.
    expect(useSessionStore.getState()).toMatchObject({ accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B', userId: 'user-1' });
    expect(JSON.parse(secureStore['couplesync_session'])).toMatchObject({ accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B' });
    expect(getSessionEpoch()).toBeGreaterThan(epochBefore);
  });

  it('uma notificação capturada no grupo anterior e ainda não enviada é descartada, não lançada no grupo novo', async () => {
    await useConsentStore.getState().load('user-1');
    await useConsentStore.getState().acceptCapture();
    mockPost.mockRejectedValue(new Error('Network Error'));
    await handleRawNotificationEvent({
      packageName: 'com.nu.production',
      title: 'Compra aprovada',
      body: 'Compra no crédito: R$ 45,90 em PADARIA DO ZE',
      timestampMs: Date.UTC(2026, 9, 5, 14, 30, 0),
    });
    expect(getPendingRetryCount()).toBe(1);

    await activateGroup({ accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B' }, groupSessionDeps(createAppQueryClient()));

    expect(getPendingRetryCount()).toBe(0);
    // O consentimento é do usuário, não do grupo: continua valendo no grupo novo.
    expect(useConsentStore.getState().record.capture.enabled).toBe(true);
  });

  it('criar/entrar sem refresh token novo mantém o guardado; ficar sem grupo ativo guarda grupo nulo', async () => {
    await activateGroup({ accessToken: 'access-B', refreshToken: null, coupleId: 'group-B' }, groupSessionDeps(createAppQueryClient()));
    expect(useSessionStore.getState()).toMatchObject({ accessToken: 'access-B', refreshToken: 'refresh-A', coupleId: 'group-B' });

    await activateGroup({ accessToken: 'access-0', refreshToken: 'refresh-0', coupleId: null }, groupSessionDeps(createAppQueryClient()));
    expect(useSessionStore.getState()).toMatchObject({ accessToken: 'access-0', refreshToken: 'refresh-0', coupleId: null, userId: 'user-1' });
  });

  it('não recria uma sessão que já foi encerrada', async () => {
    await useSessionStore.getState().clearSession();

    await activateGroup({ accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B' }, groupSessionDeps(createAppQueryClient()));

    expect(useSessionStore.getState().accessToken).toBeNull();
    expect(secureStore['couplesync_session']).toBeUndefined();
  });
});
