// O que as telas decidem a partir de GET /ai/status. A IA vem ligada para todo grupo, sem pergunta nem aceite: só
// `available` e `enabled` decidem. Tudo aqui é inventado (nomes de exemplo).
import {
  AI_CONSENT_VERSION,
  AI_GROUP_STATE_TEXT,
  AI_STATUS_NOTICE_TEXT,
  AI_STATUS_RETRY_DELAYS_MS,
  aiDestinations,
  aiGroupState,
  aiSettingsActions,
  aiStatusNotice,
  parseStoredAiStatus,
  serializeStoredAiStatus,
  aiUploadConsent,
  assistantGate,
  chatErrorChangesStatus,
  chatErrorMessage,
  groupBudgetText,
  isAssistantVisible,
  modelQuotaText,
  usageTotals,
} from '../aiStatus';
import * as aiStatusModule from '../aiStatus';
import { NETWORK_ERROR_MESSAGE, TIMEOUT_ERROR_MESSAGE } from '@/services/apiError';
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
    providers: [{ name: 'Google (Gemini)', country: 'Estados Unidos', trainsOnData: true }],
    features: { assistant: true, insights: false, education: false, weeklyEmail: false },
    budget: { callsToday: 0, callLimit: 25, resetsAtLocal: '2026-10-09T00:00:00' },
    ...overrides,
  };
}

const byAna = { userId: 'user-ana', name: 'Ana Exemplo', acceptedAtUtc: '2026-10-08T15:00:00Z' };

/** O que a API responde hoje para um grupo qualquer: ligada, sem aceite de ninguém, sem pergunta. */
const ON_WITHOUT_ACCEPTANCE = status({ enabled: true, acceptedBy: [], myAcceptance: null, onboardingPending: false });
/** O que só uma API anterior responderia: pergunta pendente. Nenhuma regra daqui olha para isso. */
const ON_WITH_OLD_QUESTION = status({ enabled: true, onboardingPending: true });

function apiError(statusCode: number, code: string, message = 'mensagem do servidor') {
  return { response: { status: statusCode, data: { code, message } } };
}

describe('versão do aceite de IA', () => {
  it('é a 2 (o texto que cita o Groq), a mesma do servidor', () => {
    expect(AI_CONSENT_VERSION).toBe(2);
  });
});

describe('destinos que o texto da análise com IA cita', () => {
  const google = { name: 'Google (Gemini)', country: 'Estados Unidos', trainsOnData: true };
  const groq = { name: 'Groq', country: 'Estados Unidos', trainsOnData: false };

  it('são os que o servidor diz ter ligados: só o Google, ou o Google e o Groq', () => {
    expect(aiDestinations(status({ providers: [google] }))).toEqual([google]);
    expect(aiDestinations(status({ providers: [google, groq] }))).toEqual([google, groq]);
  });

  it('sem status, com lista vazia ou com um status guardado da versão anterior do texto, não se sabe: null (o texto cita todos)', () => {
    expect(aiDestinations(null)).toBeNull();
    expect(aiDestinations(status({ providers: [] }))).toBeNull();
    // Guardado no aparelho antes da atualização: listava só o Google, e o servidor pode ter o Groq ligado agora.
    expect(aiDestinations(status({ consentVersion: 1, providers: [google] }))).toBeNull();
  });
});

describe('visibilidade do Assistente no Painel', () => {
  it('sem status (ainda não carregou, ou a consulta falhou) o botão não aparece', () => {
    expect(isAssistantVisible(null)).toBe(false);
  });

  it('com a IA indisponível no servidor o botão some', () => {
    expect(isAssistantVisible(status({ available: false, enabled: true }))).toBe(false);
  });

  it('com IA no servidor o botão aparece: ligada sem aceite de ninguém, e também desligada para o grupo', () => {
    expect(isAssistantVisible(ON_WITHOUT_ACCEPTANCE)).toBe(true);
    expect(isAssistantVisible(status({ enabled: false }))).toBe(true);
  });
});

describe('o que a tela do Assistente mostra', () => {
  it('carregando enquanto não há status; erro quando a consulta falhou e não há valor anterior', () => {
    expect(assistantGate(null, false)).toBe('loading');
    expect(assistantGate(null, true)).toBe('error');
  });

  it('a conversa, direto, para o grupo ligado: sem aceite de ninguém, com aceite, e mesmo com "pergunta pendente" de uma API anterior', () => {
    expect(assistantGate(ON_WITHOUT_ACCEPTANCE, false)).toBe('ready');
    expect(assistantGate(status({ enabled: true, acceptedBy: [byAna] }), false)).toBe('ready');
    expect(assistantGate(ON_WITH_OLD_QUESTION, false)).toBe('ready');
  });

  it('indisponível no servidor, ou desligada para o grupo', () => {
    expect(assistantGate(status({ available: false, enabled: true }), false)).toBe('unavailable');
    expect(assistantGate(status({ enabled: false }), false)).toBe('off-for-group');
  });

  it('com um valor anterior, uma falha de rede não tira a pessoa da conversa', () => {
    expect(assistantGate(ON_WITHOUT_ACCEPTANCE, true)).toBe('ready');
  });
});

describe('não há mais pergunta, boas-vindas, cartão de ativar nem "ativada por"', () => {
  it('o módulo não exporta mais nenhuma das regras que abriam a pergunta ou dependiam de aceite', () => {
    for (const gone of ['shouldOpenWelcome', 'welcomeView', 'leaveWelcomeIfDue', 'shouldShowActivationCard', 'activationSummary']) {
      expect(aiStatusModule).not.toHaveProperty(gone);
    }
  });
});

describe('estado em Configurações > Inteligência artificial', () => {
  it('ligada: basta haver IA e o grupo não ter desligado — com ou sem aceite de alguém', () => {
    expect(aiGroupState(ON_WITHOUT_ACCEPTANCE)).toBe('on');
    expect(aiGroupState(status({ enabled: true, acceptedBy: [byAna] }))).toBe('on');
    expect(aiGroupState(ON_WITH_OLD_QUESTION)).toBe('on');
    expect(AI_GROUP_STATE_TEXT.on).toBe('Ligada');
  });

  it('desligada para o grupo', () => {
    expect(aiGroupState(status({ enabled: false }))).toBe('off-for-group');
    // Vinda de uma API anterior, com aceites de quem já saiu da conta ou "pergunta pendente": é a mesma tela.
    expect(aiGroupState(status({ enabled: false, onboardingPending: true }))).toBe('off-for-group');
    expect(AI_GROUP_STATE_TEXT['off-for-group']).toBe('Desligada para o grupo');
  });

  it('indisponível no servidor, esteja o grupo ligado ou não', () => {
    expect(aiGroupState(status({ available: false, enabled: true }))).toBe('unavailable');
    expect(aiGroupState(status({ available: false, enabled: false }))).toBe('unavailable');
    expect(AI_GROUP_STATE_TEXT.unavailable).toBe('Indisponível no momento');
  });

  it('nenhum dos textos de estado manda ativar nem fala em aceite', () => {
    for (const text of Object.values(AI_GROUP_STATE_TEXT)) expect(text).not.toMatch(/ativ|aceit/i);
  });
});

describe('frases do Assistente por desfecho', () => {
  it('cota do grupo', () => {
    expect(chatErrorMessage(apiError(429, 'AI_DAILY_BUDGET_EXHAUSTED'))).toBe('A cota de IA do grupo para hoje acabou. Volta à meia-noite.');
  });

  it('teto global', () => {
    expect(chatErrorMessage(apiError(429, 'AI_GLOBAL_BUDGET_EXHAUSTED'))).toBe(
      'A IA do app atingiu o limite de uso de hoje. Volta à meia-noite. Os números do app continuam atualizados.',
    );
  });

  it('todos os modelos falharam', () => {
    expect(chatErrorMessage(apiError(502, 'AI_PROVIDER_FAILED'))).toBe('A IA não respondeu agora. Tente de novo em alguns minutos.');
  });

  it('IA desligada no servidor, e IA desligada para o grupo', () => {
    expect(chatErrorMessage(apiError(404, 'AI_CHAT_DISABLED'))).toBe('A análise com IA não está disponível no momento.');
    // O 403 que o servidor responde ao grupo que desligou (e que a API anterior respondia a quem não tinha aceite):
    // sempre a frase do app, em português, qualquer que seja o texto que o servidor mandou.
    const phrase = 'A IA está desligada para este grupo. Para voltar a usar, ligue em Configurações > Inteligência artificial.';
    expect(chatErrorMessage(apiError(403, 'AI_CONSENT_REQUIRED'))).toBe(phrase);
    expect(
      chatErrorMessage(apiError(403, 'AI_CONSENT_REQUIRED', 'A análise com IA está desligada para este grupo. Ative em Configurações > Inteligência artificial.')),
    ).toBe(phrase);
    expect(phrase).not.toMatch(/ative|aceit/i);
  });

  it('limite por hora: a mensagem do servidor', () => {
    expect(chatErrorMessage(apiError(429, 'CHAT_RATE_LIMITED', 'Limite de 30 mensagens por hora atingido. Tente novamente mais tarde.'))).toBe(
      'Limite de 30 mensagens por hora atingido. Tente novamente mais tarde.',
    );
  });

  it('sem internet e tempo esgotado', () => {
    expect(chatErrorMessage({ code: 'ERR_NETWORK', message: 'Network Error' })).toBe(NETWORK_ERROR_MESSAGE);
    expect(chatErrorMessage({ code: 'ECONNABORTED', message: 'timeout of 30000ms exceeded' })).toBe(TIMEOUT_ERROR_MESSAGE);
  });

  it('qualquer outra coisa: frase da tela, nunca texto técnico', () => {
    expect(chatErrorMessage(new Error('boom'))).toBe('Erro ao enviar mensagem. Tente novamente.');
  });

  it('quais erros dizem que o status mudou no servidor (a tela consulta de novo)', () => {
    expect(chatErrorChangesStatus(apiError(403, 'AI_CONSENT_REQUIRED'))).toBe(true);
    expect(chatErrorChangesStatus(apiError(404, 'AI_CHAT_DISABLED'))).toBe(true);
    expect(chatErrorChangesStatus(apiError(429, 'AI_DAILY_BUDGET_EXHAUSTED'))).toBe(false);
    expect(chatErrorChangesStatus({ code: 'ERR_NETWORK' })).toBe(false);
  });
});

describe('consumo', () => {
  it('cota do modelo ainda não conhecida', () => {
    expect(modelQuotaText({ name: 'gemini', model: 'gemini-flash-lite-latest', calls: 4, limit: null, percentUsed: null, exhaustedToday: false })).toBe(
      '4 chamadas hoje · cota ainda não conhecida',
    );
  });

  it('cota conhecida: percentual', () => {
    expect(modelQuotaText({ name: 'gemini', model: 'gemini-2.5-flash', calls: 11, limit: 40, percentUsed: 27, exhaustedToday: false })).toBe(
      '11 chamadas hoje · 27% da cota diária (40)',
    );
  });

  it('uma chamada no singular e modelo esgotado hoje', () => {
    expect(modelQuotaText({ name: 'gemini', model: 'm', calls: 1, limit: null, percentUsed: null, exhaustedToday: true })).toBe(
      '1 chamada hoje · cota ainda não conhecida · esgotado hoje',
    );
  });

  it('orçamento do grupo', () => {
    expect(groupBudgetText({ callsToday: 3, callLimit: 25 })).toBe('3 de 25 chamadas à IA do grupo hoje');
  });

  it('totais de hoje e do período', () => {
    const days = [
      { day: '2026-10-06', calls: 4, inputTokens: 300, outputTokens: 60, failures: 1 },
      { day: '2026-10-07', calls: 0, inputTokens: 0, outputTokens: 0, failures: 0 },
      { day: '2026-10-08', calls: 2, inputTokens: 200, outputTokens: 40, failures: 0 },
    ];
    expect(usageTotals(days)).toEqual({
      today: { calls: 2, tokens: 240, failures: 0 },
      period: { calls: 6, tokens: 600, failures: 1 },
    });
    expect(usageTotals([])).toEqual({ today: { calls: 0, tokens: 0, failures: 0 }, period: { calls: 0, tokens: 0, failures: 0 } });
  });
});

describe('Painel sem status nenhum: a pessoa sempre sabe o que está acontecendo com a IA (revisão 1, I3)', () => {
  it('com status (do servidor ou guardado no aparelho) não há aviso', () => {
    expect(aiStatusNotice(status(), false, false)).toBe('none');
    expect(aiStatusNotice(status(), true, true)).toBe('none');
    expect(aiStatusNotice(status({ available: false }), true, false)).toBe('none');
  });

  it('consulta em andamento, falha com nova tentativa marcada e falha sem mais tentativas têm cada uma o seu aviso', () => {
    expect(aiStatusNotice(null, false, false)).toBe('checking');
    expect(aiStatusNotice(null, true, true)).toBe('retrying');
    expect(aiStatusNotice(null, true, false)).toBe('failed');
    expect(AI_STATUS_NOTICE_TEXT.checking).toBe('Verificando a análise com IA…');
    expect(AI_STATUS_NOTICE_TEXT.retrying).toBe('Não foi possível verificar a análise com IA. Tentando de novo…');
    expect(AI_STATUS_NOTICE_TEXT.failed).toBe('Não foi possível verificar a análise com IA. Verifique a internet.');
  });

  it('as novas tentativas cobrem a API acordando (mais de dois minutos) e são poucas', () => {
    expect(AI_STATUS_RETRY_DELAYS_MS.length).toBeGreaterThanOrEqual(3);
    expect(AI_STATUS_RETRY_DELAYS_MS.length).toBeLessThanOrEqual(6);
    expect(AI_STATUS_RETRY_DELAYS_MS.reduce((sum, delay) => sum + delay, 0)).toBeGreaterThanOrEqual(120_000);
    expect([...AI_STATUS_RETRY_DELAYS_MS]).toEqual([...AI_STATUS_RETRY_DELAYS_MS].sort((a, b) => a - b));
  });
});

describe('o último status guardado no aparelho', () => {
  const on = status({ enabled: true, acceptedBy: [byAna], myAcceptance: { acceptedAtUtc: byAna.acceptedAtUtc }, onboardingPending: false });

  it('o que foi guardado volta igual, para o mesmo grupo', () => {
    expect(parseStoredAiStatus(serializeStoredAiStatus('couple-1', on), 'couple-1')).toEqual(on);
    expect(parseStoredAiStatus(serializeStoredAiStatus('couple-1', status()), 'couple-1')).toEqual(status());
  });

  it('o formato v1 que a versão anterior do app gravou continua sendo lido, com ou sem aceite e com a pergunta pendente', () => {
    // Texto exatamente como o app instalado deixou no aparelho (grupo desligado, pergunta pendente).
    const leftByInstalledApp =
      '{"v":1,"coupleId":"couple-1","status":{"available":true,"enabled":false,"consentVersion":2,"acceptedBy":[],"myAcceptance":null,' +
      '"onboardingPending":true,"weeklyEmailEnabled":false,"emailVerified":false,"emailConfigured":false,' +
      '"providers":[{"name":"Google (Gemini)","country":"Estados Unidos","trainsOnData":true}],' +
      '"features":{"assistant":true,"insights":false,"education":false,"weeklyEmail":false},' +
      '"budget":{"callsToday":0,"callLimit":25,"resetsAtLocal":"2026-10-09T00:00:00"}}}';
    expect(parseStoredAiStatus(leftByInstalledApp, 'couple-1')).toEqual(status());
    // E o que a API responde hoje (ligada, sem aceite de ninguém) é guardado e lido no mesmo formato.
    expect(serializeStoredAiStatus('couple-1', ON_WITHOUT_ACCEPTANCE)).toContain('"v":1');
    expect(parseStoredAiStatus(serializeStoredAiStatus('couple-1', ON_WITHOUT_ACCEPTANCE), 'couple-1')).toEqual(ON_WITHOUT_ACCEPTANCE);
  });

  it('de outro grupo, vazio, corrompido, de outra versão ou sem a forma esperada: é como não ter nada guardado', () => {
    expect(parseStoredAiStatus(serializeStoredAiStatus('couple-1', on), 'couple-2')).toBeNull();
    expect(parseStoredAiStatus(null, 'couple-1')).toBeNull();
    expect(parseStoredAiStatus('', 'couple-1')).toBeNull();
    expect(parseStoredAiStatus('{não é json', 'couple-1')).toBeNull();
    expect(parseStoredAiStatus('null', 'couple-1')).toBeNull();
    expect(parseStoredAiStatus(JSON.stringify({ v: 2, coupleId: 'couple-1', status: on }), 'couple-1')).toBeNull();
    expect(parseStoredAiStatus(JSON.stringify({ v: 1, coupleId: 'couple-1', status: { available: true } }), 'couple-1')).toBeNull();
    expect(parseStoredAiStatus(JSON.stringify({ v: 1, coupleId: 'couple-1', status: { ...on, acceptedBy: [{ userId: 1 }] } }), 'couple-1')).toBeNull();
    expect(parseStoredAiStatus(JSON.stringify({ v: 1, coupleId: 'couple-1', status: { ...on, enabled: 'sim' } }), 'couple-1')).toBeNull();
  });
});

describe('Configurações > Inteligência artificial: o que cada pessoa pode fazer', () => {
  it('ligada (com ou sem aceite de alguém): só desligar para o grupo', () => {
    expect(aiSettingsActions(ON_WITHOUT_ACCEPTANCE)).toEqual({ turnOn: false, turnOffForGroup: true });
    expect(aiSettingsActions(status({ enabled: true, acceptedBy: [byAna], myAcceptance: { acceptedAtUtc: byAna.acceptedAtUtc } }))).toEqual({
      turnOn: false,
      turnOffForGroup: true,
    });
  });

  it('desligada para o grupo: só ligar de novo', () => {
    expect(aiSettingsActions(status({ enabled: false }))).toEqual({ turnOn: true, turnOffForGroup: false });
  });

  it('IA indisponível no servidor: não há o que ligar', () => {
    expect(aiSettingsActions(status({ available: false, enabled: false }))).toEqual({ turnOn: false, turnOffForGroup: false });
  });

  it('não existe mais "retirar meu aceite" nem "ativar" que leve a uma pergunta', () => {
    expect(Object.keys(aiSettingsActions(ON_WITHOUT_ACCEPTANCE)).sort()).toEqual(['turnOffForGroup', 'turnOn']);
  });
});

describe('extrato e Open Finance: quando o campo de IA vai ligado', () => {
  it('sempre que há IA e o grupo não desligou — sem depender de aceite de ninguém', () => {
    expect(aiUploadConsent(ON_WITHOUT_ACCEPTANCE)).toBe(true);
    expect(aiUploadConsent(ON_WITH_OLD_QUESTION)).toBe(true);
    expect(aiUploadConsent(status({ enabled: true, acceptedBy: [byAna] }))).toBe(true);
  });

  it('desligado sem status, com a IA indisponível ou com o grupo desligado', () => {
    expect(aiUploadConsent(null)).toBe(false);
    expect(aiUploadConsent(status({ enabled: false }))).toBe(false);
    expect(aiUploadConsent(status({ available: false, enabled: true }))).toBe(false);
  });
});
