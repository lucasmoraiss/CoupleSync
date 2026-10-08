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

import {
  aiStatusStorageKey,
  currentAiStatus,
  openWelcomeIfDue,
  routeAfterGroupSetup,
  selectForSession,
  selectLoadForSession,
  useAiStatusStore,
  wasWelcomeShown,
} from '../aiStatusStore';
import { AI_STATUS_RETRY_DELAYS_MS, aiStatusNotice, isAssistantVisible, serializeStoredAiStatus, shouldShowActivationCard } from '../aiStatus';
import * as SecureStore from 'expo-secure-store';
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

// ─── Revisão 1 (I3): a IA não some do Painel quando a primeira consulta falha ────────────────────────────────

function loadForSession() {
  const { userId, coupleId } = useSessionStore.getState();
  return selectLoadForSession(useAiStatusStore.getState(), userId, coupleId);
}

/** Deixa as promessas pendentes (leitura e gravação no aparelho) terminarem. */
async function settle() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

function pendingStatus() {
  let release!: (value: { data: AiStatusResponse }) => void;
  const promise = new Promise<{ data: AiStatusResponse }>((resolve) => { release = resolve; });
  return { promise, release };
}

const NETWORK_DOWN = { code: 'ERR_NETWORK', message: 'Network Error' };

afterAll(async () => {
  await clearUserData();
});

describe('o último status fica guardado no aparelho, por pessoa', () => {
  it('cada resposta do servidor é guardada na chave da pessoa, com o grupo a que pertence', async () => {
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().refresh();
    await settle();

    expect(JSON.parse(secureStore[aiStatusStorageKey('user-1')])).toEqual({ v: 1, coupleId: 'couple-1', status: ENABLED });
    expect(loadForSession()).toEqual({ fromStorage: false, retrying: false });
  });

  it('abertura a frio, servidor ainda sem responder: o botão Assistente e o cartão vêm do valor guardado', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', status());
    await signIn('user-1', 'couple-1');
    const server = pendingStatus();
    mockGetStatus.mockReturnValue(server.promise);

    const refreshing = useAiStatusStore.getState().refresh();
    await settle();

    expect(forSession().status).toEqual(status());
    expect(isAssistantVisible(forSession().status)).toBe(true);
    expect(shouldShowActivationCard(forSession().status)).toBe(true);
    expect(loadForSession().fromStorage).toBe(true);

    server.release({ data: ENABLED });
    await refreshing;
    expect(forSession().status).toEqual(ENABLED);
    expect(loadForSession().fromStorage).toBe(false);
  });

  it('abertura a frio com a consulta falhando (API acordando, sem rede): o valor guardado continua valendo', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', ENABLED);
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);

    await useAiStatusStore.getState().refresh();
    await settle();

    expect(forSession().status).toEqual(ENABLED);
    expect(forSession().loadFailed).toBe(true);
    expect(aiStatusNotice(forSession().status, forSession().loadFailed, loadForSession().retrying)).toBe('none');
  });

  it('o valor guardado de outro grupo, ou de outra pessoa, não vale', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-9', ENABLED);
    secureStore[aiStatusStorageKey('user-2')] = serializeStoredAiStatus('couple-1', ENABLED);
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockReturnValue(pendingStatus().promise);

    void useAiStatusStore.getState().refresh();
    await settle();

    expect(forSession().status).toBeNull();
  });

  it('o valor guardado nunca passa por cima do que o servidor acabou de responder', async () => {
    const stored = serializeStoredAiStatus('couple-1', ENABLED);
    let releaseRead!: (value: string) => void;
    (SecureStore.getItemAsync as jest.Mock).mockImplementationOnce(() => new Promise<string>((resolve) => { releaseRead = resolve; }));
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockResolvedValue({ data: status() });

    await useAiStatusStore.getState().refresh();
    releaseRead(stored);
    await settle();

    expect(forSession().status).toEqual(status());
    expect(loadForSession().fromStorage).toBe(false);
  });

  it('ativar e desligar também atualizam o valor guardado', async () => {
    await signIn('user-1', 'couple-1');
    mockAccept.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().activate();
    await settle();
    expect(JSON.parse(secureStore[aiStatusStorageKey('user-1')]).status.enabled).toBe(true);

    mockRevoke.mockResolvedValue({ data: status({ onboardingPending: false }) });
    await useAiStatusStore.getState().revoke('group');
    await settle();
    expect(JSON.parse(secureStore[aiStatusStorageKey('user-1')]).status.enabled).toBe(false);
  });

  it('sair da conta apaga o valor guardado, e uma resposta atrasada não o grava de volta', async () => {
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockResolvedValueOnce({ data: ENABLED });
    await useAiStatusStore.getState().refresh();
    await settle();
    expect(secureStore[aiStatusStorageKey('user-1')]).toBeDefined();
    const late = pendingStatus();
    mockGetStatus.mockReturnValueOnce(late.promise);
    const refreshing = useAiStatusStore.getState().refresh();

    await clearUserData();
    late.release({ data: ENABLED });
    await refreshing;
    await settle();

    expect(secureStore[aiStatusStorageKey('user-1')]).toBeUndefined();
  });

  it('aparelho que não lê nem grava o armazenamento: a consulta ao servidor funciona do mesmo jeito', async () => {
    (SecureStore.getItemAsync as jest.Mock).mockRejectedValueOnce(new Error('keystore'));
    (SecureStore.setItemAsync as jest.Mock).mockImplementation(async (key: string, value: string) => {
      if (key.startsWith('couplesync_ai_status_')) throw new Error('keystore');
      secureStore[key] = value;
    });
    try {
      await signIn('user-1', 'couple-1');
      mockGetStatus.mockResolvedValue({ data: ENABLED });

      await expect(useAiStatusStore.getState().refresh()).resolves.toEqual(ENABLED);
      await settle();
      expect(forSession().status).toEqual(ENABLED);
    } finally {
      (SecureStore.setItemAsync as jest.Mock).mockImplementation(async (key: string, value: string) => {
        secureStore[key] = value;
      });
    }
  });
});

describe('a consulta que falha é tentada de novo sozinha', () => {
  beforeEach(() => {
    jest.useFakeTimers();
  });

  afterEach(async () => {
    await clearUserData();
    jest.useRealTimers();
  });

  it('API acordando: a primeira consulta falha, o Painel avisa, e quando o servidor responde a IA aparece sem a pessoa fazer nada', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValueOnce(NETWORK_DOWN).mockRejectedValueOnce(NETWORK_DOWN).mockResolvedValue({ data: status() });

    await expect(useAiStatusStore.getState().refresh()).resolves.toBeNull();

    expect(aiStatusNotice(forSession().status, forSession().loadFailed, loadForSession().retrying)).toBe('retrying');
    expect(isAssistantVisible(forSession().status)).toBe(false);

    await jest.advanceTimersByTimeAsync(AI_STATUS_RETRY_DELAYS_MS[0]);
    expect(mockGetStatus).toHaveBeenCalledTimes(2);
    expect(loadForSession().retrying).toBe(true);

    await jest.advanceTimersByTimeAsync(AI_STATUS_RETRY_DELAYS_MS[1]);
    expect(mockGetStatus).toHaveBeenCalledTimes(3);
    expect(forSession()).toEqual({ status: status(), loadFailed: false, welcomeShown: false });
    expect(loadForSession()).toEqual({ fromStorage: false, retrying: false });
    expect(isAssistantVisible(forSession().status)).toBe(true);
    expect(shouldShowActivationCard(forSession().status)).toBe(true);

    await jest.advanceTimersByTimeAsync(10 * 60_000);
    expect(mockGetStatus).toHaveBeenCalledTimes(3);
  });

  it('as tentativas têm fim: depois da última o aviso fica, com o botão de verificar', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);

    await useAiStatusStore.getState().refresh();
    for (const delay of AI_STATUS_RETRY_DELAYS_MS) await jest.advanceTimersByTimeAsync(delay);

    expect(mockGetStatus).toHaveBeenCalledTimes(1 + AI_STATUS_RETRY_DELAYS_MS.length);
    expect(aiStatusNotice(forSession().status, forSession().loadFailed, loadForSession().retrying)).toBe('failed');

    await jest.advanceTimersByTimeAsync(60 * 60_000);
    expect(mockGetStatus).toHaveBeenCalledTimes(1 + AI_STATUS_RETRY_DELAYS_MS.length);
  });

  it('a tentativa marcada não roda depois de sair da conta, nem para o grupo seguinte', async () => {
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);
    await useAiStatusStore.getState().refresh();

    await clearUserData();
    await jest.advanceTimersByTimeAsync(10 * 60_000);
    expect(mockGetStatus).toHaveBeenCalledTimes(1);

    await signIn('user-1', 'couple-1');
    await useAiStatusStore.getState().refresh();
    expect(mockGetStatus).toHaveBeenCalledTimes(2);
    await useSessionStore.getState().setActiveGroup('access-2', 'couple-2', 'refresh-2');
    await jest.advanceTimersByTimeAsync(10 * 60_000);
    expect(mockGetStatus).toHaveBeenCalledTimes(2);
    expect(loadForSession().retrying).toBe(false);
  });

  it('puxar para atualizar (ou voltar ao Painel) durante a espera consulta na hora e não deixa tentativa sobrando', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValueOnce(NETWORK_DOWN).mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().refresh();
    expect(loadForSession().retrying).toBe(true);

    await expect(useAiStatusStore.getState().refresh()).resolves.toEqual(ENABLED);

    expect(mockGetStatus).toHaveBeenCalledTimes(2);
    await jest.advanceTimersByTimeAsync(10 * 60_000);
    expect(mockGetStatus).toHaveBeenCalledTimes(2);
  });

  it('duas consultas pedidas ao mesmo tempo viram uma chamada só', async () => {
    await signIn('user-1');
    const server = pendingStatus();
    mockGetStatus.mockReturnValue(server.promise);

    const first = useAiStatusStore.getState().refresh();
    const second = useAiStatusStore.getState().refresh();
    server.release({ data: ENABLED });

    await expect(Promise.all([first, second])).resolves.toEqual([ENABLED, ENABLED]);
    expect(mockGetStatus).toHaveBeenCalledTimes(1);
  });

  it('uma escrita que dá certo durante a espera encerra as tentativas (o status já é o do servidor)', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);
    await useAiStatusStore.getState().refresh();
    mockAccept.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().activate();
    await jest.advanceTimersByTimeAsync(10 * 60_000);

    expect(mockGetStatus).toHaveBeenCalledTimes(1);
    expect(forSession()).toEqual({ status: ENABLED, loadFailed: false, welcomeShown: false });
  });
});

describe('cada resposta do servidor é avisada às telas; o valor guardado não', () => {
  it('a contagem de respostas novas sobe com o servidor (consulta ou escrita) e não com a leitura do aparelho', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', status());
    await signIn('user-1', 'couple-1');
    const server = pendingStatus();
    mockGetStatus.mockReturnValue(server.promise);
    const refreshing = useAiStatusStore.getState().refresh();
    await settle();
    expect(forSession().status).not.toBeNull();
    expect(useAiStatusStore.getState().freshCount).toBe(0);

    server.release({ data: status() });
    await refreshing;
    expect(useAiStatusStore.getState().freshCount).toBe(1);

    mockAccept.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().activate();
    expect(useAiStatusStore.getState().freshCount).toBe(2);
  });
});

describe('abrir a tela de boas-vindas a partir do Painel', () => {
  const PENDING = status({ onboardingPending: true });

  async function ready() {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: PENDING });
    await useAiStatusStore.getState().refresh();
  }

  it('pergunta devida, Painel em foco e nenhuma outra tela automática na frente: abre uma vez só', async () => {
    await ready();
    const open = jest.fn();
    const deps = { isFocused: () => true, otherPromptPending: async () => false, open };

    await expect(openWelcomeIfDue(PENDING, deps)).resolves.toBe(true);
    await expect(openWelcomeIfDue(PENDING, deps)).resolves.toBe(false);

    expect(open).toHaveBeenCalledTimes(1);
    expect(wasWelcomeShown()).toBe(true);
  });

  it('o consentimento da captura vai abrir: a tela da IA espera a vez dela (não abre e não fica marcada como mostrada)', async () => {
    await ready();
    const open = jest.fn();

    await expect(openWelcomeIfDue(PENDING, { isFocused: () => true, otherPromptPending: async () => true, open })).resolves.toBe(false);

    expect(open).not.toHaveBeenCalled();
    expect(wasWelcomeShown()).toBe(false);
    // Na volta ao Painel, com a captura respondida, a pergunta da IA abre.
    await expect(openWelcomeIfDue(PENDING, { isFocused: () => true, otherPromptPending: async () => false, open })).resolves.toBe(true);
  });

  it('o Painel perdeu o foco enquanto se conferia (outra tela abriu por cima): não abre nem marca', async () => {
    await ready();
    const open = jest.fn();
    let focused = true;
    const otherPromptPending = async () => {
      focused = false;
      return false;
    };

    await expect(openWelcomeIfDue(PENDING, { isFocused: () => focused, otherPromptPending, open })).resolves.toBe(false);

    expect(open).not.toHaveBeenCalled();
    expect(wasWelcomeShown()).toBe(false);
  });

  it('a sessão mudou enquanto se conferia: não abre para a sessão seguinte', async () => {
    await ready();
    const open = jest.fn();
    const otherPromptPending = async () => {
      await useSessionStore.getState().setActiveGroup('access-2', 'couple-2', 'refresh-2');
      return false;
    };

    await expect(openWelcomeIfDue(PENDING, { isFocused: () => true, otherPromptPending, open })).resolves.toBe(false);
    expect(open).not.toHaveBeenCalled();
  });

  it('pergunta já respondida: nem confere as outras telas', async () => {
    await ready();
    const otherPromptPending = jest.fn(async () => false);
    const open = jest.fn();

    await expect(openWelcomeIfDue(status({ onboardingPending: false }), { isFocused: () => true, otherPromptPending, open })).resolves.toBe(false);

    expect(otherPromptPending).not.toHaveBeenCalled();
    expect(open).not.toHaveBeenCalled();
  });

  it('conferir as outras telas falhou: a pergunta da IA abre assim mesmo', async () => {
    await ready();
    const open = jest.fn();
    const otherPromptPending = async (): Promise<boolean> => {
      throw new Error('ponte nativa');
    };

    await expect(openWelcomeIfDue(PENDING, { isFocused: () => true, otherPromptPending, open })).resolves.toBe(true);
    expect(open).toHaveBeenCalledTimes(1);
  });

  it('depois de criar ou entrar num grupo, com o consentimento da captura para abrir: Painel, e a pergunta da IA fica para depois', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: PENDING });

    await expect(routeAfterGroupSetup(4000, async () => true)).resolves.toBe('/');

    expect(wasWelcomeShown()).toBe(false);
  });
});
