// Issue #60, item 11: de onde vem o campo de IA (`aiCategorizationConsent`) que acompanha um pedido de
// sincronização do Open Finance. A regra (aiUploadConsent) e a leitura (currentAiStatus) têm teste próprio; aqui
// é a LIGAÇÃO: os três lugares que pedem sincronização usam o status da IA da sessão atual — nunca o aceite antigo
// guardado no aparelho, nunca um valor fixo.
//
// A abertura do app (useAutoSync.ts) é exercitada de verdade: o gancho roda com um React de mentira (só o que ele
// e o zustand usam), a regra de sync.ts e o store do status são os reais, e o servidor é de mentira. As duas telas
// não são desenhadas pelo Jest daqui: para elas o teste lê o código, como os de aiScreens.test.ts.
// Tudo inventado; relógio fixo.
import * as fs from 'fs';
import * as path from 'path';

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

// O gancho só usa useEffect; o zustand usa os outros três. Fora de uma tela, o efeito roda na hora.
jest.mock('react', () => ({
  useEffect: (effect: () => unknown) => {
    effect();
  },
  useSyncExternalStore: (_subscribe: unknown, getSnapshot: () => unknown) => getSnapshot(),
  useCallback: (fn: unknown) => fn,
  useDebugValue: () => undefined,
}));

const mockAppStateListeners: Array<(state: string) => void> = [];
jest.mock('react-native', () => ({
  AppState: {
    addEventListener: (_event: string, listener: (state: string) => void) => {
      mockAppStateListeners.push(listener);
      return { remove: () => undefined };
    },
  },
}));

const mockOpenFinanceStatus = jest.fn();
const mockRequestSync = jest.fn();
const mockAiStatus = jest.fn();
jest.mock('@/services/apiClient', () => ({
  openFinanceApiClient: {
    getStatus: (...args: unknown[]) => mockOpenFinanceStatus(...args),
    requestSync: (...args: unknown[]) => mockRequestSync(...args),
  },
  aiApiClient: {
    getStatus: (...args: unknown[]) => mockAiStatus(...args),
  },
}));

import { useAutoSyncOnOpen } from '../useAutoSync';
import { useAiStatusStore } from '@/modules/ai/aiStatusStore';
import { useSessionStore } from '@/state/sessionStore';
import { clearUserData } from '@/state/userData';
import type { AiStatusResponse, OpenFinanceStatusResponse } from '@/types/api';

const NOW = Date.parse('2026-10-09T15:00:00Z');

function aiStatus(overrides: Partial<AiStatusResponse> = {}): AiStatusResponse {
  return {
    available: true,
    enabled: false,
    consentVersion: 1,
    acceptedBy: [],
    myAcceptance: null,
    onboardingPending: false,
    weeklyEmailEnabled: false,
    emailVerified: false,
    emailConfigured: false,
    providers: [],
    features: { assistant: true, insights: false, education: false, weeklyEmail: false },
    budget: { callsToday: 0, callLimit: 25, resetsAtLocal: '2026-10-10T00:00:00' },
    ...overrides,
  };
}

const ACTIVATED = aiStatus({ enabled: true, acceptedBy: [{ userId: 'user-1', name: 'Ana Exemplo', acceptedAtUtc: '2026-10-08T15:00:00Z' }] });

/** A minha conexão, sincronizada pela última vez ontem: a abertura do app pede uma sincronização. */
const STALE_CONNECTION: OpenFinanceStatusResponse = {
  available: true,
  connections: [
    {
      id: 'conn-1',
      label: 'Meus bancos',
      userId: 'user-1',
      userName: 'Ana Exemplo',
      isMine: true,
      status: 'Active',
      clientIdHint: '0a1b',
      historyMonths: 3,
      lastSyncAtUtc: '2026-10-08T06:00:00Z',
      lastErrorCode: null,
      lastErrorMessage: null,
      createdAtUtc: '2026-10-01T12:00:00Z',
      items: [{ id: 'item-1', connectorName: 'Banco Exemplo', status: 'UPDATED', executionStatus: 'SUCCESS', lastUpdatedAtUtc: null, lastErrorMessage: null, accounts: [] }],
    },
  ],
};

async function settle() {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

async function signIn(userId: string, coupleId: string) {
  await useSessionStore.getState().setSession(`access-${userId}`, `refresh-${userId}`, userId, coupleId);
}

/** O status da IA como o Painel o deixa no store ao abrir: consultado no servidor (de mentira). */
async function loadAiStatus(status: AiStatusResponse) {
  mockAiStatus.mockResolvedValueOnce({ data: status });
  await useAiStatusStore.getState().refresh();
}

beforeEach(async () => {
  jest.useFakeTimers({ now: NOW });
  await clearUserData(); // também zera "quando foi a última tentativa" do pedido automático
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockAppStateListeners.length = 0;
  mockOpenFinanceStatus.mockReset().mockResolvedValue({ data: STALE_CONNECTION });
  mockRequestSync.mockReset().mockResolvedValue({ data: {} });
  mockAiStatus.mockReset();
});

afterEach(async () => {
  await clearUserData();
  jest.useRealTimers();
});

describe('sincronização automática ao abrir o app: o campo de IA vem do status da IA da sessão', () => {
  it('grupo com a análise ativada no servidor: o pedido leva o campo', async () => {
    await signIn('user-1', 'couple-1');
    await loadAiStatus(ACTIVATED);

    useAutoSyncOnOpen(true);
    await settle();

    expect(mockRequestSync).toHaveBeenCalledTimes(1);
    expect(mockRequestSync).toHaveBeenCalledWith('conn-1', '?appOpen=true&aiCategorizationConsent=true');
  });

  it('grupo que não ativou: o pedido vai sem o campo', async () => {
    await signIn('user-1', 'couple-1');
    await loadAiStatus(aiStatus());

    useAutoSyncOnOpen(true);
    await settle();

    expect(mockRequestSync).toHaveBeenCalledWith('conn-1', '?appOpen=true');
  });

  it('status da IA ainda não consultado: sem o campo (o lado seguro)', async () => {
    await signIn('user-1', 'couple-1');

    useAutoSyncOnOpen(true);
    await settle();

    expect(mockRequestSync).toHaveBeenCalledWith('conn-1', '?appOpen=true');
  });

  it('o status ativado de OUTRO grupo não vale para o grupo de agora', async () => {
    await signIn('user-1', 'couple-1');
    await loadAiStatus(ACTIVATED);
    await useSessionStore.getState().setActiveGroup('access-2', 'couple-2');

    useAutoSyncOnOpen(true);
    await settle();

    expect(mockRequestSync).toHaveBeenCalledWith('conn-1', '?appOpen=true');
  });

  it('o valor é lido na hora do pedido: ativada enquanto o status do banco era consultado, o campo vai', async () => {
    await signIn('user-1', 'couple-1');
    await loadAiStatus(aiStatus());
    let release!: (value: { data: OpenFinanceStatusResponse }) => void;
    mockOpenFinanceStatus.mockReturnValueOnce(new Promise((resolve) => { release = resolve; }));

    useAutoSyncOnOpen(true);
    await loadAiStatus(ACTIVATED);
    release({ data: STALE_CONNECTION });
    await settle();

    expect(mockRequestSync).toHaveBeenCalledWith('conn-1', '?appOpen=true&aiCategorizationConsent=true');
  });

  it('ao voltar para o app (AppState "active") depois de 10 minutos, o novo pedido usa o status de agora', async () => {
    await signIn('user-1', 'couple-1');
    await loadAiStatus(aiStatus());
    useAutoSyncOnOpen(true);
    await settle();
    expect(mockRequestSync).toHaveBeenLastCalledWith('conn-1', '?appOpen=true');

    await loadAiStatus(ACTIVATED);
    jest.setSystemTime(NOW + 11 * 60 * 1000);
    mockAppStateListeners.forEach((listener) => listener('active'));
    await settle();

    expect(mockRequestSync).toHaveBeenCalledTimes(2);
    expect(mockRequestSync).toHaveBeenLastCalledWith('conn-1', '?appOpen=true&aiCategorizationConsent=true');
  });
});

describe('telas do Open Finance: "Sincronizar agora" e o fim do wizard', () => {
  const MOBILE_DIR = path.resolve(__dirname, '../../../..');
  const read = (relative: string) => fs.readFileSync(path.join(MOBILE_DIR, relative), 'utf8');

  it.each(['app/(main)/settings/openfinance/index.tsx', 'app/(main)/settings/openfinance/wizard.tsx'])(
    '%s: o campo de IA do pedido vem de aiUploadConsent(currentAiStatus()), e de nenhum outro lugar',
    (file) => {
      const source = read(file);
      expect(source).toContain("import { aiUploadConsent } from '@/modules/ai/aiStatus';");
      expect(source).toContain("import { currentAiStatus } from '@/modules/ai/aiStatusStore';");
      const fields = (source.match(/aiConsent:[^\r\n]*/g) ?? []).map((line) => line.trim());
      expect(fields).toEqual(['aiConsent: aiUploadConsent(currentAiStatus()),']);
      // O aceite antigo guardado no aparelho (campo aiChat do registro de consentimento) não decide nada aqui.
      expect(source).not.toMatch(/isAiChatAllowed|record\.aiChat|acceptAiChat/);
    },
  );

  it('a abertura do app (useAutoSync.ts) lê o valor só na hora do pedido', () => {
    expect(read('src/modules/openfinance/useAutoSync.ts')).toContain('aiConsent: () => aiUploadConsent(currentAiStatus()),');
  });
});
