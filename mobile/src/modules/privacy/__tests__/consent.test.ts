import {
  EMPTY_CONSENT,
  acceptAiChat,
  acceptCapture,
  declineAiChat,
  declineCapture,
  formatConsentDate,
  markCapturePromptShown,
  isAiChatAllowed,
  isCaptureAllowed,
  parseConsent,
  serializeConsent,
  setCaptureEnabled,
  shouldPromptCaptureConsent,
} from '../consent';

const NOW = '2026-10-06T12:00:00.000Z';

describe('captura de notificações', () => {
  it('sem registro nada é permitido', () => {
    expect(isCaptureAllowed(EMPTY_CONSENT)).toBe(false);
    expect(isAiChatAllowed(EMPTY_CONSENT)).toBe(false);
  });

  it('só captura depois do aceite, e o interruptor desliga na hora', () => {
    const accepted = acceptCapture(EMPTY_CONSENT, NOW);
    expect(isCaptureAllowed(accepted)).toBe(true);
    expect(accepted.capture.acceptedAt).toBe(NOW);

    const off = setCaptureEnabled(accepted, false, '2026-10-07T00:00:00.000Z');
    expect(isCaptureAllowed(off)).toBe(false);
    expect(off.capture.acceptedAt).toBe(NOW); // a data do aceite continua visível
    expect(isCaptureAllowed(setCaptureEnabled(off, true, NOW))).toBe(true);
  });

  it('o interruptor não liga sem aceite anterior', () => {
    expect(setCaptureEnabled(EMPTY_CONSENT, true, NOW)).toBe(EMPTY_CONSENT);
    expect(isCaptureAllowed(setCaptureEnabled(declineCapture(EMPTY_CONSENT, NOW), true, NOW))).toBe(false);
  });

  it('recusar registra a resposta e não liga', () => {
    const declined = declineCapture(EMPTY_CONSENT, NOW);
    expect(isCaptureAllowed(declined)).toBe(false);
    expect(declined.capture.decidedAt).toBe(NOW);
  });
});

describe('tela de consentimento para quem já usa a captura', () => {
  it('aparece uma vez: permissão concedida e nunca respondeu', () => {
    expect(shouldPromptCaptureConsent(EMPTY_CONSENT, true)).toBe(true);
  });
  it('não aparece sem a permissão de ler notificações', () => {
    expect(shouldPromptCaptureConsent(EMPTY_CONSENT, false)).toBe(false);
  });
  it('não reaparece depois de mostrada, mesmo sem resposta', () => {
    const shown = markCapturePromptShown(EMPTY_CONSENT, NOW);
    expect(shown.capture.promptShownAt).toBe(NOW);
    expect(shouldPromptCaptureConsent(shown, true)).toBe(false);
    expect(parseConsent(serializeConsent(shown)).capture.promptShownAt).toBe(NOW);
  });
  it.each([
    ['aceitou', acceptCapture(EMPTY_CONSENT, NOW)],
    ['recusou', declineCapture(EMPTY_CONSENT, NOW)],
  ])('não aparece de novo depois que o usuário %s', (_label, record) => {
    expect(shouldPromptCaptureConsent(record, true)).toBe(false);
  });
});

describe('chat com IA', () => {
  it('aceitar libera, recusar bloqueia e mudar de ideia funciona nos dois sentidos', () => {
    const accepted = acceptAiChat(EMPTY_CONSENT, NOW);
    expect(isAiChatAllowed(accepted)).toBe(true);
    const declined = declineAiChat(accepted, NOW);
    expect(isAiChatAllowed(declined)).toBe(false);
    expect(declined.aiChat.declinedAt).toBe(NOW);
    expect(isAiChatAllowed(acceptAiChat(declined, NOW))).toBe(true);
  });
  it('aceitar o chat não liga a captura e vice-versa', () => {
    expect(isCaptureAllowed(acceptAiChat(EMPTY_CONSENT, NOW))).toBe(false);
    expect(isAiChatAllowed(acceptCapture(EMPTY_CONSENT, NOW))).toBe(false);
  });
});

describe('guardar e ler', () => {
  it('ida e volta preserva o registro', () => {
    const record = acceptAiChat(acceptCapture(EMPTY_CONSENT, NOW), NOW);
    expect(parseConsent(serializeConsent(record))).toEqual(record);
  });
  it.each([[null], [undefined], [''], ['{'], ['[]'], ['{"version":99}'], ['"x"']])('lixo (%p) vira "nunca respondeu"', (raw) => {
    expect(parseConsent(raw as string | null | undefined)).toEqual(EMPTY_CONSENT);
  });
  it('"ligado" sem data de aceite não vale', () => {
    const forged = JSON.stringify({ version: 1, capture: { enabled: true, acceptedAt: null, decidedAt: NOW }, aiChat: {} });
    expect(isCaptureAllowed(parseConsent(forged))).toBe(false);
  });
});

describe('formatConsentDate', () => {
  it('lê a data no horário de Brasília', () => {
    expect(formatConsentDate('2026-10-06T12:00:00.000Z')).toBe('6 de outubro de 2026');
    expect(formatConsentDate('2026-10-07T01:30:00.000Z')).toBe('6 de outubro de 2026'); // 22:30 em Brasília
  });
  it('vazio quando não há data', () => {
    expect(formatConsentDate(null)).toBe('');
    expect(formatConsentDate('x')).toBe('');
  });
});
