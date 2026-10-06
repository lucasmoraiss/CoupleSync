import {
  CODE_LENGTH,
  RESET_UNAVAILABLE_MESSAGE,
  VERIFY_UNAVAILABLE_MESSAGE,
  getEmailFlowErrorMessage,
  isCompleteCode,
  isEmailNotConfigured,
  normalizeCode,
} from '../emailCodes';

function apiError(status: number, code: string, message: string) {
  return { response: { status, data: { code, message, traceId: 'x' } } };
}

describe('códigos por e-mail', () => {
  it('normalizeCode mantém só dígitos e corta em 6', () => {
    expect(normalizeCode(' 12 34-56 ')).toBe('123456');
    expect(normalizeCode('abc1234567890')).toBe('123456');
    expect(normalizeCode('')).toBe('');
    expect(normalizeCode('1234567').length).toBe(CODE_LENGTH);
  });

  it('isCompleteCode exige exatamente 6 dígitos', () => {
    expect(isCompleteCode('123456')).toBe(true);
    expect(isCompleteCode('12345')).toBe(false);
    expect(isCompleteCode('12345a')).toBe(false);
    expect(isCompleteCode('')).toBe(false);
  });

  it('EMAIL_NOT_CONFIGURED é reconhecido e explica que a recuperação está indisponível', () => {
    const error = apiError(503, 'EMAIL_NOT_CONFIGURED', 'O envio de e-mail não está disponível no momento.');

    expect(isEmailNotConfigured(error)).toBe(true);
    expect(getEmailFlowErrorMessage(error, 'fallback', RESET_UNAVAILABLE_MESSAGE)).toBe(RESET_UNAVAILABLE_MESSAGE);
    expect(getEmailFlowErrorMessage(error, 'fallback', VERIFY_UNAVAILABLE_MESSAGE)).toBe(VERIFY_UNAVAILABLE_MESSAGE);
    expect(RESET_UNAVAILABLE_MESSAGE).toMatch(/indisponível/);
  });

  it('outros erros usam a mensagem da API (ex.: código inválido)', () => {
    const error = apiError(400, 'INVALID_CODE', 'Código inválido ou expirado. Solicite um novo código.');

    expect(isEmailNotConfigured(error)).toBe(false);
    expect(getEmailFlowErrorMessage(error, 'fallback', RESET_UNAVAILABLE_MESSAGE)).toBe(
      'Código inválido ou expirado. Solicite um novo código.',
    );
  });

  it('sem resposta do servidor cai na mensagem de rede, não na de indisponível', () => {
    const error = { code: 'ERR_NETWORK', message: 'Network Error' };

    expect(isEmailNotConfigured(error)).toBe(false);
    expect(getEmailFlowErrorMessage(error, 'fallback', RESET_UNAVAILABLE_MESSAGE)).toMatch(/conexão/);
  });
});
