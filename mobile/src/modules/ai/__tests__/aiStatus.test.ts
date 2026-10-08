// Issue #38: o que as telas decidem a partir de GET /ai/status. Tudo aqui é inventado (nomes de exemplo).
import {
  AI_CONSENT_VERSION,
  activationSummary,
  aiUploadConsent,
  assistantGate,
  chatErrorChangesStatus,
  chatErrorMessage,
  groupBudgetText,
  isAssistantVisible,
  modelQuotaText,
  shouldShowActivationCard,
  shouldOpenWelcome,
  usageTotals,
  welcomeView,
} from '../aiStatus';
import { NETWORK_ERROR_MESSAGE, TIMEOUT_ERROR_MESSAGE } from '@/services/apiError';
import type { AiStatusResponse } from '@/types/api';

const date = (iso: string | null) => `[${(iso ?? '').slice(0, 10)}]`;

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
    providers: [{ name: 'Google (Gemini)', country: 'Estados Unidos', trainsOnData: true }],
    features: { assistant: true, insights: false, education: false, weeklyEmail: false },
    budget: { callsToday: 0, callLimit: 25, resetsAtLocal: '2026-10-09T00:00:00' },
    ...overrides,
  };
}

const byAna = { userId: 'user-ana', name: 'Ana Exemplo', acceptedAtUtc: '2026-10-08T15:00:00Z' };
const byBruno = { userId: 'user-bruno', name: 'Bruno Exemplo', acceptedAtUtc: '2026-10-09T15:00:00Z' };

function apiError(statusCode: number, code: string, message = 'mensagem do servidor') {
  return { response: { status: statusCode, data: { code, message } } };
}

describe('versão do aceite de IA', () => {
  it('é a 1, a mesma do servidor', () => {
    expect(AI_CONSENT_VERSION).toBe(1);
  });
});

describe('visibilidade do Assistente e do cartão de ativar', () => {
  it('sem status (ainda não carregou, ou a consulta falhou) nada de IA aparece no Painel', () => {
    expect(isAssistantVisible(null)).toBe(false);
    expect(shouldShowActivationCard(null)).toBe(false);
  });

  it('com a IA indisponível no servidor o botão some e não há cartão de ativar', () => {
    const off = status({ available: false, enabled: true });
    expect(isAssistantVisible(off)).toBe(false);
    expect(shouldShowActivationCard(off)).toBe(false);
  });

  it('disponível e desligada para o grupo: botão visível e cartão "Análise com IA desligada"', () => {
    expect(isAssistantVisible(status())).toBe(true);
    expect(shouldShowActivationCard(status())).toBe(true);
  });

  it('ativada: botão visível e sem cartão', () => {
    const on = status({ enabled: true, acceptedBy: [byAna] });
    expect(isAssistantVisible(on)).toBe(true);
    expect(shouldShowActivationCard(on)).toBe(false);
  });
});

describe('o que a tela do Assistente mostra', () => {
  it('carregando enquanto não há status; erro quando a consulta falhou e não há valor anterior', () => {
    expect(assistantGate(null, false)).toBe('loading');
    expect(assistantGate(null, true)).toBe('error');
  });

  it('indisponível, pedir ativação ou conversa, conforme o servidor', () => {
    expect(assistantGate(status({ available: false }), false)).toBe('unavailable');
    expect(assistantGate(status(), false)).toBe('needs-activation');
    expect(assistantGate(status({ enabled: true, acceptedBy: [byAna] }), false)).toBe('ready');
  });

  it('com um valor anterior, uma falha de rede não tira a pessoa da conversa', () => {
    expect(assistantGate(status({ enabled: true, acceptedBy: [byAna] }), true)).toBe('ready');
  });

  it('o aceite antigo guardado no aparelho não entra na decisão: só o status do servidor', () => {
    // A função nem recebe o registro local; um grupo desligado no servidor pede ativação.
    expect(assistantGate(status({ enabled: false }), false)).toBe('needs-activation');
  });
});

describe('tela de boas-vindas', () => {
  it('abre sozinha só quando o servidor diz que a pergunta está pendente e ela ainda não foi mostrada nesta sessão', () => {
    expect(shouldOpenWelcome(status(), false)).toBe(true);
    expect(shouldOpenWelcome(status(), true)).toBe(false);
    expect(shouldOpenWelcome(status({ onboardingPending: false }), false)).toBe(false);
    expect(shouldOpenWelcome(status({ available: false }), false)).toBe(false);
    expect(shouldOpenWelcome(null, false)).toBe(false);
  });

  it('ninguém ativou: pergunta de ativação', () => {
    expect(welcomeView(status(), 'user-ana')).toEqual({ kind: 'ask' });
  });

  it('o parceiro já ativou: aviso com o nome de quem ativou', () => {
    expect(welcomeView(status({ enabled: true, acceptedBy: [byBruno] }), 'user-ana')).toEqual({
      kind: 'activated-by-other',
      names: 'Bruno',
    });
  });

  it('dois outros membros ativaram: os dois nomes', () => {
    const carla = { userId: 'user-carla', name: 'Carla Exemplo', acceptedAtUtc: '2026-10-10T15:00:00Z' };
    expect(welcomeView(status({ enabled: true, acceptedBy: [byBruno, carla] }), 'user-ana')).toEqual({
      kind: 'activated-by-other',
      names: 'Bruno e Carla',
    });
  });

  it('IA indisponível: nada a mostrar (a tela volta ao Painel)', () => {
    expect(welcomeView(status({ available: false }), 'user-ana')).toEqual({ kind: 'unavailable' });
  });

  it('a própria pessoa já ativou: nada a perguntar', () => {
    const mine = status({ enabled: true, acceptedBy: [byAna], myAcceptance: { acceptedAtUtc: byAna.acceptedAtUtc }, onboardingPending: false });
    expect(welcomeView(mine, 'user-ana')).toEqual({ kind: 'done' });
  });
});

describe('estado em Configurações > Inteligência artificial', () => {
  it('desligada', () => {
    expect(activationSummary(status(), date)).toBe('Desligada');
  });

  it('ativada por uma pessoa, com a data', () => {
    expect(activationSummary(status({ enabled: true, acceptedBy: [byAna] }), date)).toBe('Ativada por Ana Exemplo em [2026-10-08]');
  });

  it('ativada por duas pessoas', () => {
    expect(activationSummary(status({ enabled: true, acceptedBy: [byAna, byBruno] }), date)).toBe(
      'Ativada por Ana Exemplo em [2026-10-08] e por Bruno Exemplo em [2026-10-09]',
    );
  });

  it('indisponível no servidor', () => {
    expect(activationSummary(status({ available: false }), date)).toBe('Indisponível no momento');
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

  it('IA desligada no servidor e grupo sem ativação', () => {
    expect(chatErrorMessage(apiError(404, 'AI_CHAT_DISABLED'))).toBe('A análise com IA não está disponível no momento.');
    expect(chatErrorMessage(apiError(403, 'AI_CONSENT_REQUIRED'))).toBe(
      'A análise com IA está desligada para este grupo. Ative em Configurações > Inteligência artificial.',
    );
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
    expect(groupBudgetText({ callsToday: 3, callLimit: 25 })).toBe('3 de 25 perguntas do grupo hoje');
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

describe('extrato: quando as descrições podem ir para a IA', () => {
  it('só com a IA disponível e o grupo ativado no servidor', () => {
    expect(aiUploadConsent(null)).toBe(false);
    expect(aiUploadConsent(status())).toBe(false);
    expect(aiUploadConsent(status({ available: false, enabled: true }))).toBe(false);
    expect(aiUploadConsent(status({ enabled: true, acceptedBy: [byAna] }))).toBe(true);
  });
});
