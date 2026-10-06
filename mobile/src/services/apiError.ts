// Único ponto que transforma um erro de chamada à API em texto para o usuário.
// Módulo sem dependência de React Native (só duck typing do erro do axios), para ser testável com Jest.
//
// Formato de erro da API (todo 4xx/5xx de toda rota):
//   { code: 'UPPER_SNAKE_CASE', message: 'texto em português', errors?: { campo: ['mensagem'] }, traceId }
// A `message` já vem pronta para exibir. Qualquer outra coisa (proxy devolvendo HTML, corpo vazio,
// statusText em inglês) NUNCA é mostrada: cai nas mensagens por status ou no texto de reserva da tela.

export const NETWORK_ERROR_MESSAGE = 'Sem conexão. Verifique sua internet e tente novamente.';
export const TIMEOUT_ERROR_MESSAGE = 'Tempo esgotado. Tente novamente.';
export const SERVER_ERROR_MESSAGE = 'Servidor com problemas. Tente novamente mais tarde.';
export const DEFAULT_ERROR_MESSAGE = 'Algo deu errado. Tente novamente.';

const STATUS_MESSAGES: Readonly<Record<number, string>> = {
  401: 'Sua sessão expirou. Entre novamente.',
  403: 'Você não tem permissão para fazer isso.',
  404: 'Não encontramos o que você procurava.',
  408: TIMEOUT_ERROR_MESSAGE,
  413: 'O arquivo enviado é grande demais.',
  429: 'Muitas tentativas. Aguarde um instante e tente novamente.',
};

export interface ApiErrorBody {
  readonly code: string;
  readonly message: string;
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

interface ErrorLike {
  readonly code?: unknown;
  readonly message?: unknown;
  readonly response?: { readonly status?: unknown; readonly data?: unknown };
}

function asErrorLike(error: unknown): ErrorLike | null {
  return typeof error === 'object' && error !== null ? (error as ErrorLike) : null;
}

/** Corpo da resposta no formato de erro da API, ou null se a resposta tem outra forma. */
export function getApiErrorBody(error: unknown): ApiErrorBody | null {
  const data = asErrorLike(error)?.response?.data;
  if (typeof data !== 'object' || data === null) return null;
  const { code, message, errors } = data as Record<string, unknown>;
  if (typeof code !== 'string' || typeof message !== 'string' || message.trim() === '') return null;
  return { code, message, errors: errors as ApiErrorBody['errors'] };
}

/** `code` estável da API (ex.: 'COUPLE_REQUIRED'), ou undefined. */
export function getApiErrorCode(error: unknown): string | undefined {
  return getApiErrorBody(error)?.code;
}

/** Status HTTP da resposta, ou undefined quando não houve resposta (rede/tempo esgotado). */
export function getApiErrorStatus(error: unknown): number | undefined {
  const status = asErrorLike(error)?.response?.status;
  return typeof status === 'number' ? status : undefined;
}

function isTimeout(error: ErrorLike): boolean {
  return (
    error.code === 'ECONNABORTED' ||
    error.code === 'ETIMEDOUT' ||
    (typeof error.message === 'string' && /timeout/i.test(error.message))
  );
}

/** Todas as mensagens por campo de um erro de validação, sem repetição. */
function collectFieldMessages(errors: ApiErrorBody['errors']): string[] {
  if (typeof errors !== 'object' || errors === null) return [];
  const messages: string[] = [];
  for (const list of Object.values(errors)) {
    if (!Array.isArray(list)) continue;
    for (const item of list) {
      if (typeof item === 'string' && item.trim() !== '' && !messages.includes(item)) {
        messages.push(item);
      }
    }
  }
  return messages;
}

/**
 * Mensagem em português para mostrar ao usuário.
 * Ordem: sem resposta (tempo esgotado / sem conexão) -> mensagem da API -> mensagem do status
 * -> `fallback` da tela (o que a tela estava tentando fazer) -> mensagem genérica.
 */
export function getApiErrorMessage(error: unknown, fallback: string = DEFAULT_ERROR_MESSAGE): string {
  const err = asErrorLike(error);
  if (!err) return fallback;

  const status = getApiErrorStatus(error);
  if (status === undefined) {
    // Sem resposta do servidor: só vale dizer "sem conexão" para erros de requisição de verdade.
    if (isTimeout(err)) return TIMEOUT_ERROR_MESSAGE;
    if (err.code === 'ERR_NETWORK' || err.message === 'Network Error') return NETWORK_ERROR_MESSAGE;
    return fallback;
  }

  const body = getApiErrorBody(error);
  if (body) {
    const fieldMessages = collectFieldMessages(body.errors);
    // Validação de formulário: mostra todos os problemas de uma vez, um por linha.
    return fieldMessages.length > 1 ? fieldMessages.join('\n') : body.message;
  }

  if (status >= 500) return SERVER_ERROR_MESSAGE;
  return STATUS_MESSAGES[status] ?? fallback;
}
