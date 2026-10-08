// Issue #38, revisão 1 (I5): "uma pessoa ativa, a outra vê quem ativou" e Configurações > Inteligência artificial
// (desligar para o grupo, retirar o próprio aceite), de ponta a ponta no app: o estado real (aiStatusStore), as
// regras que as telas usam (aiStatus) e um servidor de mentira com as regras do servidor de verdade (um aceite
// vale para o grupo; quem ainda não viu o aviso tem a pergunta pendente). O Jest daqui não desenha telas: o que
// aparece em cada uma é conferido pelo que as telas leem; o toque de verdade é o fluxo Maestro 03.
// Tudo inventado (nomes de exemplo).
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
  accepted: new Map<string, string>(),
  answered: new Set<string>(),
  clock: 0,
  calls: [] as string[],
  me(): string {
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    return require('@/state/sessionStore').useSessionStore.getState().userId as string;
  },
  status() {
    const me = this.me();
    const acceptedBy = [...this.accepted.entries()].map(([userId, acceptedAtUtc]) => ({ userId, name: this.names[userId], acceptedAtUtc }));
    const enabled = acceptedBy.length > 0;
    const mine = this.accepted.get(me);
    const othersActivated = acceptedBy.some((acceptance) => acceptance.userId !== me);
    return {
      data: {
        available: true,
        enabled,
        consentVersion: 1,
        acceptedBy,
        myAcceptance: mine ? { acceptedAtUtc: mine } : null,
        // A pergunta é devida a quem não aceitou e ainda não respondeu (nem viu que outra pessoa ativou).
        onboardingPending: !mine && !this.answered.has(me) && (!enabled || othersActivated),
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
      mockServer.accepted.set(mockServer.me(), `2026-10-0${mockServer.clock}T12:00:00Z`);
      return mockServer.status();
    },
    revoke: async (scope: 'mine' | 'group') => {
      mockServer.calls.push(`revoke:${scope}:${mockServer.me()}`);
      if (scope === 'group') mockServer.accepted.clear();
      else mockServer.accepted.delete(mockServer.me());
      return mockServer.status();
    },
    updatePreferences: async (body: { onboardingAnswered?: boolean }) => {
      mockServer.calls.push(`preferences:${JSON.stringify(body)}:${mockServer.me()}`);
      if (body.onboardingAnswered) mockServer.answered.add(mockServer.me());
      return mockServer.status();
    },
  },
}));

import { activationSummary, aiSettingsActions, isAssistantVisible, shouldOpenWelcome, shouldShowActivationCard, welcomeView } from '../aiStatus';
import { currentAiStatus, useAiStatusStore, wasWelcomeShown } from '../aiStatusStore';
import { clearUserData } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';

const date = (iso: string | null) => (iso ?? '').slice(0, 10);

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

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockServer.accepted.clear();
  mockServer.answered.clear();
  mockServer.clock = 0;
  mockServer.calls = [];
});

afterAll(async () => {
  await clearUserData();
});

describe('uma pessoa ativa e a outra vê quem ativou', () => {
  it('Caio ativa na pergunta; Davi, ao abrir o app, é avisado de que Caio ativou, com Entendi e Desligar para o grupo', async () => {
    const first = await openAppAs('caio');
    expect(shouldOpenWelcome(first, wasWelcomeShown())).toBe(true);
    expect(welcomeView(first, 'caio')).toEqual({ kind: 'ask' });
    expect(shouldShowActivationCard(first)).toBe(true);

    await store().activate();

    expect(mockServer.calls).toEqual(['accept:1:caio']);
    expect(welcomeView(now(), 'caio')).toEqual({ kind: 'done' });
    expect(shouldShowActivationCard(now())).toBe(false);
    expect(isAssistantVisible(now())).toBe(true);

    const seenByDavi = await openAppAs('davi');
    // A tela de boas-vindas abre sozinha para o Davi e vira o aviso com o primeiro nome de quem ativou.
    expect(shouldOpenWelcome(seenByDavi, wasWelcomeShown())).toBe(true);
    expect(welcomeView(seenByDavi, 'davi')).toEqual({ kind: 'activated-by-other', names: 'Caio' });
    expect(shouldShowActivationCard(seenByDavi)).toBe(false);
    expect(isAssistantVisible(seenByDavi)).toBe(true);

    // "Entendi": a resposta fica no servidor, a análise continua ativada e o aviso não volta.
    await store().answerOnboarding();

    expect(mockServer.calls).toEqual(['accept:1:caio', 'preferences:{"onboardingAnswered":true}:davi']);
    expect(now().enabled).toBe(true);
    expect(shouldOpenWelcome(now(), false)).toBe(false);
    expect(shouldOpenWelcome(await openAppAs('davi'), false)).toBe(false);
  });

  it('no aviso, Davi escolhe Desligar para o grupo: a análise desliga para os dois e o cartão de ativar volta ao Painel', async () => {
    await openAppAs('caio');
    await store().activate();
    await openAppAs('davi');

    await store().revoke('group');

    expect(mockServer.calls).toEqual(['accept:1:caio', 'revoke:group:davi']);
    expect(now().enabled).toBe(false);
    expect(shouldShowActivationCard(now())).toBe(true);

    const seenByCaio = await openAppAs('caio');
    expect(seenByCaio.enabled).toBe(false);
    expect(activationSummary(seenByCaio, date)).toBe('Desligada');
    expect(shouldShowActivationCard(seenByCaio)).toBe(true);
  });
});

describe('Configurações > Inteligência artificial', () => {
  it('Davi vê "Ativada por Caio Teste em …", pode desligar para o grupo, e não tem aceite próprio para retirar', async () => {
    await openAppAs('caio');
    await store().activate();
    const seenByDavi = await openAppAs('davi');

    expect(activationSummary(seenByDavi, date)).toBe('Ativada por Caio Teste em 2026-10-01');
    expect(aiSettingsActions(seenByDavi)).toEqual({ activate: false, turnOffForGroup: true, withdrawMine: false });

    await store().revoke('group');

    expect(activationSummary(now(), date)).toBe('Desligada');
    expect(aiSettingsActions(now())).toEqual({ activate: true, turnOffForGroup: false, withdrawMine: false });
    expect(shouldShowActivationCard(now())).toBe(true);
    // O botão do Assistente continua no Painel (a IA existe); o Assistente é que pede para ativar.
    expect(isAssistantVisible(now())).toBe(true);
  });

  it('os dois aceitaram: retirar o meu aceite não desliga o grupo; quando o último retira, desliga', async () => {
    await openAppAs('caio');
    await store().activate();
    await openAppAs('davi');
    await store().activate();

    expect(activationSummary(now(), date)).toBe('Ativada por Caio Teste em 2026-10-01 e por Davi Teste em 2026-10-02');
    expect(aiSettingsActions(now())).toEqual({ activate: false, turnOffForGroup: true, withdrawMine: true });

    await store().revoke('mine');

    expect(now().enabled).toBe(true);
    expect(activationSummary(now(), date)).toBe('Ativada por Caio Teste em 2026-10-01');
    expect(aiSettingsActions(now())).toEqual({ activate: false, turnOffForGroup: true, withdrawMine: false });

    await openAppAs('caio');
    expect(aiSettingsActions(now()).withdrawMine).toBe(true);
    await store().revoke('mine');

    expect(mockServer.calls.slice(-2)).toEqual(['revoke:mine:davi', 'revoke:mine:caio']);
    expect(activationSummary(now(), date)).toBe('Desligada');
    expect(aiSettingsActions(now())).toEqual({ activate: true, turnOffForGroup: false, withdrawMine: false });
  });

  it('o que cada pessoa vê é dela: depois de trocar de pessoa no aparelho, nada do status anterior sobra', async () => {
    await openAppAs('caio');
    await store().activate();
    expect(now().myAcceptance).not.toBeNull();

    await clearUserData();
    await useSessionStore.getState().setSession('access-davi', 'refresh-davi', 'davi', 'grupo-1');

    expect(currentAiStatus()).toBeNull();
  });
});
