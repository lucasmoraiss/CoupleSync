import {
  DEFAULT_ERROR_MESSAGE,
  NETWORK_ERROR_MESSAGE,
  SERVER_ERROR_MESSAGE,
  TIMEOUT_ERROR_MESSAGE,
  getApiErrorCode,
  getApiErrorMessage,
  getApiErrorStatus,
} from '../apiError';

/** Erro como o axios entrega: com resposta do servidor. */
function httpError(status: number, data: unknown, extra: Record<string, unknown> = {}) {
  return { isAxiosError: true, message: `Request failed with status code ${status}`, response: { status, data }, ...extra };
}

describe('getApiErrorMessage', () => {
  it('usa a mensagem do formato de erro da API', () => {
    const error = httpError(409, { code: 'EMAIL_ALREADY_IN_USE', message: 'Já existe uma conta com esse e-mail.', traceId: 'x' });
    expect(getApiErrorMessage(error)).toBe('Já existe uma conta com esse e-mail.');
  });

  it('prefere a mensagem da API ao texto de reserva da tela', () => {
    const error = httpError(404, { code: 'GOAL_NOT_FOUND', message: 'Meta não encontrada.' });
    expect(getApiErrorMessage(error, 'Não foi possível atualizar a meta.')).toBe('Meta não encontrada.');
  });

  it('em validação com vários campos mostra todas as mensagens, uma por linha', () => {
    const error = httpError(400, {
      code: 'VALIDATION_ERROR',
      message: "'E-mail' não é um endereço de email válido.",
      errors: {
        Email: ["'E-mail' não é um endereço de email válido."],
        Password: ["'Senha' deve ter no mínimo 8 caracteres."],
      },
    });
    expect(getApiErrorMessage(error)).toBe(
      "'E-mail' não é um endereço de email válido.\n'Senha' deve ter no mínimo 8 caracteres.",
    );
  });

  it('em validação de um campo só usa a message', () => {
    const error = httpError(400, {
      code: 'VALIDATION_ERROR',
      message: 'O prazo deve ser hoje ou uma data futura.',
      errors: { Deadline: ['O prazo deve ser hoje ou uma data futura.'] },
    });
    expect(getApiErrorMessage(error)).toBe('O prazo deve ser hoje ou uma data futura.');
  });

  it('tempo esgotado tem mensagem própria', () => {
    expect(getApiErrorMessage({ isAxiosError: true, code: 'ECONNABORTED', message: 'timeout of 30000ms exceeded' })).toBe(
      TIMEOUT_ERROR_MESSAGE,
    );
    expect(getApiErrorMessage({ isAxiosError: true, code: 'ETIMEDOUT', message: 'x' })).toBe(TIMEOUT_ERROR_MESSAGE);
  });

  it('sem conexão tem mensagem própria', () => {
    expect(getApiErrorMessage({ isAxiosError: true, code: 'ERR_NETWORK', message: 'Network Error' })).toBe(
      NETWORK_ERROR_MESSAGE,
    );
  });

  it('nunca mostra texto em inglês que não esteja no formato da API', () => {
    // proxy/CDN devolvendo HTML ou texto puro; statusText em inglês; corpo vazio
    expect(getApiErrorMessage(httpError(502, '<html>Bad Gateway</html>'))).toBe(SERVER_ERROR_MESSAGE);
    expect(getApiErrorMessage(httpError(500, { message: 'An unexpected error occurred.' }))).toBe(SERVER_ERROR_MESSAGE);
    expect(getApiErrorMessage(httpError(404, ''), 'Falhou')).toBe('Não encontramos o que você procurava.');
    expect(getApiErrorMessage(httpError(400, { title: 'One or more validation errors occurred.' }), 'Dados inválidos.')).toBe(
      'Dados inválidos.',
    );
  });

  it('erro 5xx no formato da API usa a mensagem neutra da API', () => {
    const error = httpError(500, { code: 'INTERNAL_SERVER_ERROR', message: 'Ocorreu um erro inesperado. Tente novamente em instantes.' });
    expect(getApiErrorMessage(error)).toBe('Ocorreu um erro inesperado. Tente novamente em instantes.');
  });

  it('usa o texto de reserva para erros que não são de requisição, e a mensagem genérica sem reserva', () => {
    expect(getApiErrorMessage(new Error('boom'), 'Não foi possível salvar.')).toBe('Não foi possível salvar.');
    expect(getApiErrorMessage(undefined)).toBe(DEFAULT_ERROR_MESSAGE);
    expect(getApiErrorMessage('texto solto')).toBe(DEFAULT_ERROR_MESSAGE);
  });
});

describe('getApiErrorCode / getApiErrorStatus', () => {
  it('devolve o code estável da API', () => {
    expect(getApiErrorCode(httpError(403, { code: 'COUPLE_REQUIRED', message: 'Conecte-se.' }))).toBe('COUPLE_REQUIRED');
  });

  it('devolve undefined quando o corpo não está no formato da API', () => {
    expect(getApiErrorCode(httpError(500, 'oops'))).toBeUndefined();
    expect(getApiErrorCode(new Error('x'))).toBeUndefined();
  });

  it('devolve o status HTTP, ou undefined sem resposta', () => {
    expect(getApiErrorStatus(httpError(409, {}))).toBe(409);
    expect(getApiErrorStatus({ isAxiosError: true, code: 'ERR_NETWORK' })).toBeUndefined();
  });
});
