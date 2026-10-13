// Duas pessoas no mesmo grupo e a análise com IA, de ponta a ponta no app: o estado real (aiStatusStore), as regras
// que as telas usam (aiStatus) e um servidor de mentira com as regras do servidor de verdade — a IA vem ligada para
// todo grupo, sem aceite; "desligar para o grupo" desliga para todos; "ligar de novo" religa. O mesmo servidor de
// mentira também sabe se comportar como a API ANTERIOR a esta entrega (ligada só com aceite, pergunta pendente),
// que é o que o app encontra se a API voltar atrás. O Jest daqui não desenha telas: o que aparece em cada uma é
// conferido pelo que as telas leem; o toque de verdade é o fluxo Maestro 03. Tudo inventado (nomes de exemplo).
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

const mockServer = {
  names: { caio: 'Caio Teste', davi: 'Davi Teste' } as Record<string, string>,
  /** `current`: a API desta entrega. `previous`: a de antes (o grupo só liga com aceite, e a pergunta fica pendente). */
  rules: 'current' as 'current' | 'previous',
  accepted: new Map<string, string>(),
  offForGroup: false,
  clock: 0,
  calls: [] as string[],
  me(): string {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    return require('@/state/sessionStore').useSessionStore.getState().userId as string;
  },
  status() {
    const me = this.me();
    const acceptedBy = [...this.accepted.entries()].map(([userId, acceptedAtUtc]) => ({ userId, name: this.names[userId], acceptedAtUtc }));
    const mine = this.accepted.get(me);
    return {
      data: {
        available: true,
        enabled: this.rules === 'current' ? !this.offForGroup : acceptedBy.length > 0,
        consentVersion: 2,
        acceptedBy,
        myAcceptance: mine ? { acceptedAtUtc: mine } : null,
        onboardingPending: this.rules === 'current' ? false : !mine,
        weeklyEmailEnabled: false,
        emailVerified: false,
        emailConfigured: false,
        providers: [{ name: 'Google (Gemini)', country: 'Estados Unidos', trainsOnData: true }],
        features: { assistant: true, insights: false, education: false, weeklyEmail: false },
        budget: { callsToday: 0, callLimit: 25, resetsAtLocal: '2026-10-09T00:00:00' },
      },
    };
  },
};

jest.mock('@/services/apiClient', () => ({
  aiApiClient: {
    getStatus: async () => mockServer.status(),
    accept: async (version: number) => {
      mockServer.calls.push(`accept:${version}:${mockServer.me()}`);
      mockServer.clock += 1;
      mockServer.offForGroup = false;
      mockServer.accepted.set(mockServer.me(), `2026-10-0${mockServer.clock}T12:00:00Z`);
      return mockServer.status();
    },
    revoke: async (scope: 'mine' | 'group') => {
      mockServer.calls.push(`revoke:${scope}:${mockServer.me()}`);
      if (scope === 'group') {
        mockServer.offForGroup = true;
        mockServer.accepted.clear();
      } else {
        mockServer.accepted.delete(mockServer.me());
      }
      return mockServer.status();
    },
    updatePreferences: async (body: { onboardingAnswered?: boolean }) => {
      mockServer.calls.push(`preferences:${JSON.stringify(body)}:${mockServer.me()}`);
      return mockServer.status();
    },
  },
}));

import { AI_GROUP_STATE_TEXT, aiGroupState, aiSettingsActions, aiUploadConsent, assistantGate, isAssistantVisible } from '../aiStatus';
import { currentAiStatus, useAiStatusStore } from '../aiStatusStore';
import { clearUserData } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';

/** Como num outro aparelho: sai quem estava e entra a outra pessoa, no mesmo grupo. */
async function openAppAs(userId: 'caio' | 'davi') {
  await clearUserData();
  await useSessionStore.getState().setSession(`access-${userId}`, `refresh-${userId}`, userId, 'grupo-1');
  const status = await useAiStatusStore.getState().refresh();
  if (!status) throw new Error('o status não carregou');
  return status;
}

const store = () => useAiStatusStore.getState();
const now = () => currentAiStatus()!;
const stateText = () => AI_GROUP_STATE_TEXT[aiGroupState(now())];

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockServer.rules = 'current';
  mockServer.accepted.clear();
  mockServer.offForGroup = false;
  mockServer.clock = 0;
  mockServer.calls = [];
});

afterAll(async () => {
  await clearUserData();
});

describe('grupo novo: a IA já está ligada para as duas pessoas, sem ninguém ter aceitado nada', () => {
  it('Caio e Davi abrem o app: Assistente direto, campo de IA do extrato ligado, "Ligada" em Configurações, e nada foi pedido ao servidor além do status', async () => {
    for (const person of ['caio', 'davi'] as const) {
      const seen = await openAppAs(person);

      expect(seen.acceptedBy).toEqual([]);
      expect(isAssistantVisible(seen)).toBe(true);
      expect(assistantGate(seen, false)).toBe('ready');
      expect(aiUploadConsent(seen)).toBe(true);
      expect(stateText()).toBe('Ligada');
      expect(aiSettingsActions(seen)).toEqual({ turnOn: false, turnOffForGroup: true });
    }

    // Nenhum aceite, nenhuma resposta a pergunta: o app só leu.
    expect(mockServer.calls).toEqual([]);
  });
});

describe('Configurações > Inteligência artificial: desligar para o grupo e ligar de novo', () => {
  it('Davi desliga para o grupo: desliga para os dois, o Assistente manda ligar em Configurações e o extrato vai sem IA', async () => {
    await openAppAs('davi');

    await store().turnOffForGroup();

    expect(mockServer.calls).toEqual(['revoke:group:davi']);
    expect(stateText()).toBe('Desligada para o grupo');
    expect(aiSettingsActions(now())).toEqual({ turnOn: true, turnOffForGroup: false });

    const seenByCaio = await openAppAs('caio');
    expect(seenByCaio.enabled).toBe(false);
    expect(stateText()).toBe('Desligada para o grupo');
    expect(assistantGate(seenByCaio, false)).toBe('off-for-group');
    expect(aiUploadConsent(seenByCaio)).toBe(false);
    // O botão do Assistente continua no Painel (a IA existe); a tela dele é que diz que o grupo desligou.
    expect(isAssistantVisible(seenByCaio)).toBe(true);
  });

  it('Caio liga de novo, direto (uma chamada só, sem pergunta): volta para os dois', async () => {
    await openAppAs('davi');
    await store().turnOffForGroup();
    await openAppAs('caio');

    await store().turnOn();

    expect(mockServer.calls).toEqual(['revoke:group:davi', 'accept:2:caio']);
    expect(stateText()).toBe('Ligada');
    expect(assistantGate(now(), false)).toBe('ready');

    const seenByDavi = await openAppAs('davi');
    expect(stateText()).toBe('Ligada');
    expect(assistantGate(seenByDavi, false)).toBe('ready');
    expect(aiUploadConsent(seenByDavi)).toBe(true);
    // Ligada por outra pessoa: Davi não é perguntado nem avisado de nada, e pode desligar.
    expect(aiSettingsActions(seenByDavi)).toEqual({ turnOn: false, turnOffForGroup: true });
  });
});

describe('se a API voltar para a versão anterior (grupo só liga com aceite, pergunta pendente)', () => {
  it('o grupo sem aceite aparece como desligado, nada abre sozinho, e "Ligar de novo" funciona nela também', async () => {
    mockServer.rules = 'previous';

    const seen = await openAppAs('caio');

    expect(seen.onboardingPending).toBe(true);
    expect(seen.enabled).toBe(false);
    expect(stateText()).toBe('Desligada para o grupo');
    expect(assistantGate(seen, false)).toBe('off-for-group');
    expect(aiSettingsActions(seen)).toEqual({ turnOn: true, turnOffForGroup: false });
    expect(mockServer.calls).toEqual([]);

    await store().turnOn();

    // A API anterior exige a versão 2 do texto: é a que vai.
    expect(mockServer.calls).toEqual(['accept:2:caio']);
    expect(stateText()).toBe('Ligada');
    expect(assistantGate(now(), false)).toBe('ready');

    // E o desligar para o grupo dela (que apaga os aceites) aparece como desligada.
    await store().turnOffForGroup();
    expect(stateText()).toBe('Desligada para o grupo');
  });
});

describe('o que cada pessoa vê é dela', () => {
  it('depois de trocar de pessoa no aparelho, nada do status anterior sobra', async () => {
    await openAppAs('caio');
    await store().turnOffForGroup();
    expect(now().enabled).toBe(false);

    await clearUserData();
    await useSessionStore.getState().setSession('access-davi', 'refresh-davi', 'davi', 'grupo-1');

    expect(currentAiStatus()).toBeNull();
  });
});
