// O status da IA guardado por pessoa e por grupo. Sair da conta, entrar outro usuário ou trocar de grupo nunca
// deixa o valor de um valer para o outro, e resposta atrasada é descartada. A IA vem ligada para todo grupo: o
// store só consulta, desliga para o grupo e liga de novo — não há pergunta, aceite a retirar nem tela a abrir.
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

import { aiStatusStorageKey, currentAiStatus, selectForSession, selectLoadForSession, useAiStatusStore } from '../aiStatusStore';
import * as aiStatusStoreModule from '../aiStatusStore';
import { AI_STATUS_RETRY_DELAYS_MS, aiStatusNotice, aiStatusNoticeView, assistantGate, isAssistantVisible, serializeStoredAiStatus } from '../aiStatus';
import * as SecureStore from 'expo-secure-store';
import { clearUserData, resetUserCaches } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';
import type { AiStatusResponse } from '@/types/api';

function status(overrides: Partial<AiStatusResponse> = {}): AiStatusResponse {
  return {
    available: true,
    enabled: false,
    consentVersion: 2,
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

    expect(forSession()).toEqual({ status: ENABLED, loadFailed: true });
  });

  it('sair da conta apaga o status: o próximo usuário no aparelho não herda nada', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().refresh();

    await clearUserData();
    await signIn('user-2');

    expect(useAiStatusStore.getState().status).toBeNull();
    expect(forSession()).toEqual({ status: null, loadFailed: false });
    expect(currentAiStatus()).toBeNull();
  });

  it('depois de trocar de grupo, o status do grupo anterior não vale', async () => {
    await signIn('user-1', 'couple-1');
    mockGetStatus.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().refresh();

    await useSessionStore.getState().setActiveGroup('access-2', 'couple-2', 'refresh-2');

    expect(forSession()).toEqual({ status: null, loadFailed: false });
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

describe('ligar de novo e desligar para o grupo', () => {
  it('ligar de novo usa POST /ai/consent com a versão 2 (a rota que a API anterior e a nova têm) e guarda o status devolvido', async () => {
    await signIn('user-1');
    mockAccept.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().turnOn();

    expect(mockAccept.mock.calls).toEqual([[2]]);
    expect(forSession().status).toEqual(ENABLED);
  });

  it('desligar para o grupo usa o escopo "group"; o escopo "mine" (retirar o meu aceite) não é mais pedido', async () => {
    await signIn('user-1');
    mockRevoke.mockResolvedValue({ data: status({ onboardingPending: false }) });

    await useAiStatusStore.getState().turnOffForGroup();

    expect(mockRevoke.mock.calls).toEqual([['group']]);
    expect(forSession().status?.enabled).toBe(false);
  });

  it('o store não tem mais pergunta a responder, aceite a retirar nem tela a marcar; e nada do que ele faz chama PATCH /ai/preferences', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: status({ onboardingPending: true }) });
    mockAccept.mockResolvedValue({ data: ENABLED });
    mockRevoke.mockResolvedValue({ data: status() });

    const store = useAiStatusStore.getState();
    for (const gone of ['activate', 'revoke', 'answerOnboarding', 'markWelcomeShown', 'welcomeShown']) {
      expect(store).not.toHaveProperty(gone);
    }

    await store.refresh();
    await store.turnOffForGroup();
    await store.turnOn();

    expect(mockUpdatePreferences).not.toHaveBeenCalled();
  });

  it('uma escrita que falha sobe o erro para a tela e não muda o que estava guardado', async () => {
    await signIn('user-1');
    mockGetStatus.mockResolvedValue({ data: status() });
    await useAiStatusStore.getState().refresh();
    const failure = { response: { status: 503, data: { code: 'AI_UNAVAILABLE', message: 'A análise com IA não está disponível no momento.' } } };
    mockAccept.mockRejectedValue(failure);

    await expect(useAiStatusStore.getState().turnOn()).rejects.toBe(failure);

    expect(forSession()).toEqual({ status: status(), loadFailed: false });
  });
});

describe('nada abre sozinho por causa da IA', () => {
  it('o módulo não exporta mais rota de boas-vindas nem função que decida para onde ir ou que abra tela', () => {
    for (const gone of ['AI_WELCOME_ROUTE', 'HOME_ROUTE', 'routeAfterGroupSetup', 'openWelcomeIfDue', 'wasWelcomeShown']) {
      expect(aiStatusStoreModule).not.toHaveProperty(gone);
    }
  });

  it('uma API anterior que ainda responda "pergunta pendente" e "desligada" é só guardada: o Assistente pede para ligar, e ligar resolve', async () => {
    await signIn('user-1');
    const fromOldApi = status({ enabled: false, onboardingPending: true });
    mockGetStatus.mockResolvedValue({ data: fromOldApi });

    await expect(useAiStatusStore.getState().refresh()).resolves.toEqual(fromOldApi);

    expect(forSession()).toEqual({ status: fromOldApi, loadFailed: false });
    expect(assistantGate(forSession().status, false)).toBe('off-for-group');

    mockAccept.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().turnOn();
    expect(assistantGate(forSession().status, false)).toBe('ready');
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

  it('abertura a frio, servidor ainda sem responder: o botão Assistente vem do valor guardado', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', status());
    await signIn('user-1', 'couple-1');
    const server = pendingStatus();
    mockGetStatus.mockReturnValue(server.promise);

    const refreshing = useAiStatusStore.getState().refresh();
    await settle();

    expect(forSession().status).toEqual(status());
    expect(isAssistantVisible(forSession().status)).toBe(true);
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

  it('ligar de novo e desligar também atualizam o valor guardado', async () => {
    await signIn('user-1', 'couple-1');
    mockAccept.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().turnOn();
    await settle();
    expect(JSON.parse(secureStore[aiStatusStorageKey('user-1')]).status.enabled).toBe(true);

    mockRevoke.mockResolvedValue({ data: status({ onboardingPending: false }) });
    await useAiStatusStore.getState().turnOffForGroup();
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
    expect(forSession()).toEqual({ status: status(), loadFailed: false });
    expect(loadForSession()).toEqual({ fromStorage: false, retrying: false });
    expect(isAssistantVisible(forSession().status)).toBe(true);

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

  it('"Verificar agora" depois da falha: o aviso volta a "verificando" enquanto a consulta não responde', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);
    await useAiStatusStore.getState().refresh();
    for (const delay of AI_STATUS_RETRY_DELAYS_MS) await jest.advanceTimersByTimeAsync(delay);
    const server = pendingStatus();
    mockGetStatus.mockReturnValue(server.promise);

    const checking = useAiStatusStore.getState().refresh();

    expect(aiStatusNotice(forSession().status, forSession().loadFailed, loadForSession().retrying)).toBe('checking');
    server.release({ data: status() });
    await checking;
    expect(aiStatusNotice(forSession().status, forSession().loadFailed, loadForSession().retrying)).toBe('none');
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

  // Revisão 2: ativar pelo cartão com a consulta da abertura ainda no ar (API acordando).
  it('a resposta atrasada de uma consulta não passa por cima do resultado de uma escrita feita depois dela', async () => {
    await signIn('user-1');
    const slowRead = pendingStatus();
    mockGetStatus.mockReturnValueOnce(slowRead.promise);
    const reading = useAiStatusStore.getState().refresh();
    mockAccept.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().turnOn();
    // A consulta começou antes de ativar: o que ela traz é de antes.
    slowRead.release({ data: status({ enabled: false }) });
    await reading;
    await jest.advanceTimersByTimeAsync(0);

    expect(forSession().status).toEqual(ENABLED);
    expect(JSON.parse(secureStore[aiStatusStorageKey('user-1')]).status).toEqual(ENABLED);
    expect(useAiStatusStore.getState().freshCount).toBe(1);
  });

  it('a falha atrasada de uma consulta, depois de uma escrita que deu certo, não vira aviso nem nova tentativa', async () => {
    await signIn('user-1');
    let fail!: (reason: unknown) => void;
    mockGetStatus.mockReturnValueOnce(new Promise((_, reject) => { fail = reject; }));
    const reading = useAiStatusStore.getState().refresh();
    mockAccept.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().turnOn();
    fail(NETWORK_DOWN);
    await reading;
    await jest.advanceTimersByTimeAsync(10 * 60_000);

    expect(forSession()).toEqual({ status: ENABLED, loadFailed: false });
    expect(mockGetStatus).toHaveBeenCalledTimes(1);
  });

  it('a consulta pedida depois de uma escrita é nova: não se junta à que começou antes', async () => {
    await signIn('user-1');
    const slowRead = pendingStatus();
    mockGetStatus.mockReturnValueOnce(slowRead.promise);
    const before = useAiStatusStore.getState().refresh();
    mockAccept.mockResolvedValue({ data: ENABLED });
    await useAiStatusStore.getState().turnOn();
    const OFF_AGAIN = status({ enabled: false, onboardingPending: false });
    mockGetStatus.mockResolvedValueOnce({ data: OFF_AGAIN });

    await expect(useAiStatusStore.getState().refresh()).resolves.toEqual(OFF_AGAIN);
    slowRead.release({ data: status({ enabled: false }) });
    await before;

    expect(mockGetStatus).toHaveBeenCalledTimes(2);
    expect(forSession().status).toEqual(OFF_AGAIN);
  });

  it('uma escrita que dá certo durante a espera encerra as tentativas (o status já é o do servidor)', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);
    await useAiStatusStore.getState().refresh();
    mockAccept.mockResolvedValue({ data: ENABLED });

    await useAiStatusStore.getState().turnOn();
    await jest.advanceTimersByTimeAsync(10 * 60_000);

    expect(mockGetStatus).toHaveBeenCalledTimes(1);
    expect(forSession()).toEqual({ status: ENABLED, loadFailed: false });
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
    await useAiStatusStore.getState().turnOn();
    expect(useAiStatusStore.getState().freshCount).toBe(2);
  });
});

// ─── Issue #60: acabamentos da revisão ───────────────────────────────────────────────────────────────────────

describe('sair da conta sem o status ter sido consultado nesta abertura do app (issue #60, item 7)', () => {
  it('quem abre o app já sem grupo e sai da conta não deixa a cópia da abertura anterior no aparelho', async () => {
    // A cópia ficou de uma abertura anterior, quando a pessoa ainda tinha grupo.
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', ENABLED);
    // Nesta abertura ela já não tem grupo (foi removida): nenhuma tela consulta o status, o store fica sem dono.
    await useSessionStore.getState().setSession('access', 'refresh', 'user-1', null);
    expect(useAiStatusStore.getState().ownerUserId).toBeNull();

    await clearUserData();
    await settle();

    expect(secureStore[aiStatusStorageKey('user-1')]).toBeUndefined();
  });

  it('a cópia de outra pessoa que usou o aparelho antes não é tocada', async () => {
    secureStore[aiStatusStorageKey('user-2')] = serializeStoredAiStatus('couple-2', ENABLED);
    await useSessionStore.getState().setSession('access', 'refresh', 'user-1', null);

    await clearUserData();
    await settle();

    expect(secureStore[aiStatusStorageKey('user-2')]).toBeDefined();
  });

  it('o limpador rodado ao entrar numa conta (login) não apaga a cópia de quem está entrando', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', ENABLED);
    await signIn('user-1');

    await resetUserCaches();
    await settle();

    expect(secureStore[aiStatusStorageKey('user-1')]).toBeDefined();
  });
});

describe('o aviso do Painel sobre a IA, do jeito que a pessoa o vê e usa (issue #60, item 8)', () => {
  beforeEach(() => {
    jest.useFakeTimers();
  });

  afterEach(async () => {
    await clearUserData();
    jest.useRealTimers();
  });

  /** O que o Painel desenha: o mesmo cálculo da tela, com o que o gancho useAiStatus entrega. */
  function notice() {
    return aiStatusNoticeView(forSession().status, forSession().loadFailed, loadForSession().retrying, useAiStatusStore.getState().refresh);
  }

  it('abrindo com a API dormindo: "Verificando…" sem botão; falhou: "Tentando de novo…" com "Verificar agora"; o toque consulta na hora e o aviso some quando o servidor responde', async () => {
    await signIn('user-1');
    const first = pendingStatus();
    mockGetStatus.mockReturnValueOnce(first.promise);

    const opening = useAiStatusStore.getState().refresh();
    expect(notice()).toEqual({ text: 'Verificando a análise com IA…', icon: 'sparkles-outline', checkNow: null });

    first.release(Promise.reject(NETWORK_DOWN) as never);
    await opening;
    expect(notice()).toMatchObject({ text: 'Não foi possível verificar a análise com IA. Tentando de novo…', icon: 'cloud-offline-outline' });
    expect(notice()?.checkNow).toEqual(expect.any(Function));
    expect(mockGetStatus).toHaveBeenCalledTimes(1);

    // A pessoa toca em "Verificar agora" antes da nova tentativa marcada (3 s).
    const server = pendingStatus();
    mockGetStatus.mockReturnValueOnce(server.promise);
    notice()!.checkNow!();

    expect(mockGetStatus).toHaveBeenCalledTimes(2);
    expect(notice()).toEqual({ text: 'Verificando a análise com IA…', icon: 'sparkles-outline', checkNow: null });

    server.release({ data: status() });
    await settle();
    expect(notice()).toBeNull();
    expect(isAssistantVisible(forSession().status)).toBe(true);
    // A tentativa que estava marcada foi cancelada pelo toque: nada mais é consultado.
    await jest.advanceTimersByTimeAsync(10 * 60_000);
    expect(mockGetStatus).toHaveBeenCalledTimes(2);
  });

  it('acabadas as tentativas: "Verifique a internet." com o botão, que continua funcionando', async () => {
    await signIn('user-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);
    await useAiStatusStore.getState().refresh();
    for (const delay of AI_STATUS_RETRY_DELAYS_MS) await jest.advanceTimersByTimeAsync(delay);
    const calls = mockGetStatus.mock.calls.length;

    expect(notice()).toMatchObject({ text: 'Não foi possível verificar a análise com IA. Verifique a internet.', icon: 'cloud-offline-outline' });

    mockGetStatus.mockResolvedValue({ data: ENABLED });
    notice()!.checkNow!();
    await settle();

    expect(mockGetStatus).toHaveBeenCalledTimes(calls + 1);
    expect(notice()).toBeNull();
    expect(forSession().status).toEqual(ENABLED);
  });

  it('com um valor guardado no aparelho não há aviso, mesmo com a consulta falhando', async () => {
    secureStore[aiStatusStorageKey('user-1')] = serializeStoredAiStatus('couple-1', ENABLED);
    await signIn('user-1');
    mockGetStatus.mockRejectedValue(NETWORK_DOWN);

    await useAiStatusStore.getState().refresh();
    await settle();

    expect(forSession().status).toEqual(ENABLED);
    expect(notice()).toBeNull();
  });
});
