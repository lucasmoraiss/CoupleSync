// Issue #38: o status da IA guardado por pessoa e por grupo. Sair da conta, entrar outro usuário ou trocar de
// grupo nunca deixa o valor de um valer para o outro, e resposta atrasada é descartada.
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

const mockGetStatus = jest.fn();
const mockAccept = jest.fn();
const mockRevoke = jest.fn();
const mockUpdatePreferences = jest.fn();
jest.mock('@/services/apiClient', () => ({
  aiApiClient: {
    getStatus: (...args: unknown[]) => mockGetStatus(...args),
    accept: (...args: unknown[]) => mockAccept(...args),
    revoke: (...args: unknown[]) => mockRevoke(...args),
    updatePreferences: (...args: unknown[]) => mockUpdatePreferences(...args),
  },
}));

import { currentAiStatus, routeAfterGroupSetup, selectForSession, useAiStatusStore, wasWelcomeShown } from '../aiStatusStore';
import { clearUserData } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';
import type { AiStatusResponse } from '@/types/api';

function status(overrides: Partial<AiStatusResponse> = {}): AiStatusResponse {
  return {
    available: true,
    enabled: false,
    consentVersion: 1,
    acceptedBy: [],
    myAcceptance: null,
    onboardingPending: true,
    weeklyEmailEnabled: false,
    emailVerified: false,
    emailConfigured: false,
    providers: [],
    features: { assistant: true, insights: false, education: false, weeklyEmail: false },
    budget: { callsToday: 0, callLimit: 25, resetsAtLocal: '2026-10-09T00:00:00' },
    ...overrides,
  };
}

const ENABLED = status({ enabled: true, onboardingPending: false, myAcceptance: { acceptedAtUtc: '2026-10-08T15:00:00Z' } });

async function signIn(userId: string, coupleId = 'couple-1') {
  await useSessionStore.getState().setSession(`access-${userId}`, `refresh-${userId}`, userId, coupleId);
}

function forSession() {
  const { userId, coupleId } = useSessionStore.getState();
  return selectForSession(useAiStatusStore.getState(), userId, coupleId);
}

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockGetStatus.mockReset();
  mockAccept.mockReset();
  mockRevoke.mockReset();
  mockUpdatePreferences.mockReset();
});

describe('consulta do status', () => {
  it('guarda o que o servidor respondeu, para o usuário e o grupo da sessão', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: status() });

    await expect(useAiStatusStore.getState().refresh()).resolves.toEqual(status());

    expect(forSession().status).toEqual(status());
    expect(currentAiStatus()).toEqual(status());
  });

  it('sem sessão, ou sem grupo, não consulta nada', async () => {
    await expect(useAiStatusStore.getState().refresh()).resolves.toBeNull();
    await useSessionStore.getState().setSession('access', 'refresh', 'user-1', null as unknown as string);
    await expect(useAiStatusStore.getState().refresh()).resolves.toBeNull();
    expect(mockGetStatus).not.toHaveBeenCalled();
  });

  it('uma falha marca "não carregou" e mantém o último valor conhecido', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValueOnce({ data: ENABLED });
    await useAiStatusStore.getState().refresh();
    mockGetStatus.mockRejectedValueOnce({ code: 'ERR_NETWORK', message: 'Network Error' });

    await expect(useAiStatusStore.getState().refresh()).resolves.toBeNull();

    expect(forSession()).toEqual({ status: ENABLED, loadFailed: true, welcomeShown: false });
  });

  it('sair da conta apaga o status: o próximo usuário no aparelho não herda nada', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().refresh();
    useAiStatusStore.getState().markWelcomeShown();

    await clearUserData();
    await signIn('user-2');

    expect(useAiStatusStore.getState().status).toBeNull();
    expect(forSession()).toEqual({ status: null, loadFailed: false, welcomeShown: false });
    expect(currentAiStatus()).toBeNull();
  });

  it('depois de trocar de grupo, o status do grupo anterior não vale', async () => {
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().refresh();
    useAiStatusStore.getState().markWelcomeShown();

    await useSessionStore.getState().setActiveGroup('access-2', 'couple-2', 'refresh-2');

    expect(forSession()).toEqual({ status: null, loadFailed: false, welcomeShown: false });
    expect(currentAiStatus()).toBeNull();
  });

  it('a resposta que chega depois de sair da conta é descartada', async () => {
    await signIn('user-1');
    let release: ((value: { data: AiStatusResponse }) => void) | undefined;
    mockGetStatus.mockReturnValue(new Promise((resolve) => { release = resolve; }));

    const pending = useAiStatusStore.getState().refresh();
    await clearUserData();
    await signIn('user-2');
    release!({ data: ENABLED });

    await expect(pending).resolves.toBeNull();
    expect(useAiStatusStore.getState().status).toBeNull();
    expect(currentAiStatus()).toBeNull();
  });
});

describe('ativar, desligar e responder à pergunta', () => {
  it('ativar envia a versão atual do texto e guarda o status devolvido', async () => {
    await signIn('user-1');
    mockAccept.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().activate();

    expect(mockAccept).toHaveBeenCalledWith(1);
    expect(forSession().status).toEqual(ENABLED);
  });

  it('desligar para o grupo e retirar o próprio aceite usam o escopo certo', async () => {
    await signIn('user-1');
    mockRevoke.mockResolvedValue({ data: status({ onboardingPending: false }) });

    await useAiStatusStore.getState().revoke('group');
    await useAiStatusStore.getState().revoke('mine');

    expect(mockRevoke.mock.calls).toEqual([['group'], ['mine']]);
    expect(forSession().status?.enabled).toBe(false);
  });

  it('"Agora não" grava a resposta no servidor (a pergunta não volta em outro aparelho)', async () => {
    await signIn('user-1');
    mockUpdatePreferences.mockResolvedValue({ data: status({ onboardingPending: false }) });

    await useAiStatusStore.getState().answerOnboarding();

    expect(mockUpdatePreferences).toHaveBeenCalledWith({ onboardingAnswered: true });
    expect(forSession().status?.onboardingPending).toBe(false);
  });

  it('uma escrita que falha sobe o erro para a tela e não muda o que estava guardado', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: status() });
    await useAiStatusStore.getState().refresh();
    const failure = { response: { status: 503, data: { code: 'AI_UNAVAILABLE', message: 'A análise com IA não está disponível no momento.' } } };
    mockAccept.mockRejectedValue(failure);

    await expect(useAiStatusStore.getState().activate()).rejects.toBe(failure);

    expect(forSession()).toEqual({ status: status(), loadFailed: false, welcomeShown: false });
  });

  it('a tela de boas-vindas fica marcada como mostrada só para o dono atual', async () => {
    await signIn('user-1', 'couple-1');
    useAiStatusStore.getState().markWelcomeShown();
    expect(forSession().welcomeShown).toBe(true);

    await useSessionStore.getState().setActiveGroup('access-2', 'couple-2', 'refresh-2');
    expect(forSession().welcomeShown).toBe(false);
  });
});

describe('depois de criar ou entrar num grupo', () => {
  it('pergunta pendente: vai para a tela de boas-vindas e marca como mostrada (o Painel não abre de novo)', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: status({ onboardingPending: true }) });

    await expect(routeAfterGroupSetup()).resolves.toBe('/(main)/ai/welcome');

    expect(wasWelcomeShown()).toBe(true);
  });

  it('pergunta já respondida, ou IA indisponível no servidor: Painel', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValueOnce({ data: status({ onboardingPending: false }) });
    await expect(routeAfterGroupSetup()).resolves.toBe('/');

    mockGetStatus.mockResolvedValueOnce({ data: status({ available: false, onboardingPending: false }) });
    await expect(routeAfterGroupSetup()).resolves.toBe('/');
    expect(wasWelcomeShown()).toBe(false);
  });

  it('erro ao consultar o status: Painel', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValue({ code: 'ERR_NETWORK', message: 'Network Error' });

    await expect(routeAfterGroupSetup()).resolves.toBe('/');
    expect(wasWelcomeShown()).toBe(false);
  });

  it('servidor demorando: não segura a entrada no app, vai para o Painel', async () => {
    await signIn('user-1');
    mockGetStatus.mockReturnValue(new Promise(() => undefined));

    await expect(routeAfterGroupSetup(20)).resolves.toBe('/');
  });
});
