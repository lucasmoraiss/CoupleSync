// O JavaScript que JÁ está instalado nos aparelhos (até o OTA desta entrega chegar) continua decidindo pelo código
// antigo. Aqui ficam, copiadas como foram publicadas (mobile/src/modules/ai/aiStatus.ts, app 1.3.0), as regras dele
// que dependiam de aceite e de pergunta — elas saíram do código do app, e este arquivo é o registro do que o app
// instalado faz com a resposta NOVA de GET /api/v1/ai/status. Os valores da resposta são os que a API prova devolver
// (backend: AiActivationTests.ForTheInstalledApp_TheStatusOfAnyGroupMeansOnWithoutAQuestion_AndTheChatAnswers).
// Não alterar as cópias: o que está instalado não muda. Tudo inventado (nomes de exemplo).
import { parseStoredAiStatus, serializeStoredAiStatus } from '../aiStatus';
import type { AiStatusResponse } from '@/types/api';

// ─── Cópia do app instalado ──────────────────────────────────────────────────────────────────────────────────
const installed = {
  shouldOpenWelcome: (status: AiStatusResponse | null, alreadyShown: boolean): boolean =>
    status?.available === true && status.onboardingPending && !alreadyShown,
  shouldShowActivationCard: (status: AiStatusResponse | null): boolean => status?.available === true && !status.enabled,
  assistantGate: (status: AiStatusResponse | null, loadFailed: boolean): string => {
    if (!status) return loadFailed ? 'error' : 'loading';
    if (!status.available) return 'unavailable';
    return status.enabled ? 'ready' : 'needs-activation';
  },
  aiUploadConsent: (status: AiStatusResponse | null): boolean => status?.available === true && status.enabled,
  activationSummary: (status: AiStatusResponse, formatDate: (iso: string | null) => string): string => {
    if (!status.available) return 'Indisponível no momento';
    if (!status.enabled || status.acceptedBy.length === 0) return 'Desligada';
    return `Ativada ${status.acceptedBy.map((acceptance) => `por ${acceptance.name} em ${formatDate(acceptance.acceptedAtUtc)}`).join(' e ')}`;
  },
  aiSettingsActions: (status: AiStatusResponse) => ({
    activate: status.available && !status.enabled,
    turnOffForGroup: status.enabled,
    withdrawMine: status.enabled && status.myAcceptance !== null,
  }),
};

// ─── A resposta nova da API ──────────────────────────────────────────────────────────────────────────────────
function fromNewApi(overrides: Partial<AiStatusResponse> = {}): AiStatusResponse {
  return {
    available: true,
    enabled: true,
    consentVersion: 2,
    acceptedBy: [],
    myAcceptance: null,
    onboardingPending: false,
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
const date = (iso: string | null) => `[${(iso ?? '').slice(0, 10)}]`;

describe('API nova + JavaScript já instalado', () => {
  const groups: readonly [string, AiStatusResponse][] = [
    ['grupo que nunca aceitou nada (novo, "Agora não", só aceite da versão 1, aceites revogados)', fromNewApi()],
    ['grupo com aceite da versão 2', fromNewApi({ acceptedBy: [byAna], myAcceptance: { acceptedAtUtc: byAna.acceptedAtUtc } })],
  ];

  it.each(groups)('%s: ligado, sem pergunta, sem cartão de ativar, Assistente direto, extrato com IA', (_name, status) => {
    expect(installed.shouldOpenWelcome(status, false)).toBe(false);
    expect(installed.shouldShowActivationCard(status)).toBe(false);
    expect(installed.assistantGate(status, false)).toBe('ready');
    expect(installed.aiUploadConsent(status)).toBe(true);
    // O status que o app instalado guarda no aparelho (formato v1) é lido de volta por ele e pelo app novo.
    expect(parseStoredAiStatus(serializeStoredAiStatus('couple-1', status), 'couple-1')).toEqual(status);
  });

  it('em Configurações > IA o app instalado oferece desligar para o grupo, e não oferece "Ativar" nem "Retirar meu aceite" a quem não aceitou', () => {
    expect(installed.aiSettingsActions(fromNewApi())).toEqual({ activate: false, turnOffForGroup: true, withdrawMine: false });
  });

  it('LIMITAÇÃO CONHECIDA E ACEITA: no app instalado a linha de resumo diz "Desligada" enquanto ninguém do grupo tem aceite, até o OTA chegar', () => {
    // A IA está ligada e funcionando (as outras regras acima); só esta linha de texto fica errada no app antigo.
    expect(installed.activationSummary(fromNewApi(), date)).toBe('Desligada');
    expect(installed.activationSummary(fromNewApi({ acceptedBy: [byAna] }), date)).toBe('Ativada por Ana Exemplo em [2026-10-08]');
  });

  it('grupo que desligou de propósito: o app instalado mostra desligado e o "Ativar" dele (POST /ai/consent, versão 2) religa', () => {
    const off = fromNewApi({ enabled: false });
    expect(installed.shouldOpenWelcome(off, false)).toBe(false);
    expect(installed.shouldShowActivationCard(off)).toBe(true);
    expect(installed.assistantGate(off, false)).toBe('needs-activation');
    expect(installed.aiUploadConsent(off)).toBe(false);
    expect(installed.aiSettingsActions(off)).toEqual({ activate: true, turnOffForGroup: false, withdrawMine: false });
    expect(installed.activationSummary(off, date)).toBe('Desligada');
  });
});
