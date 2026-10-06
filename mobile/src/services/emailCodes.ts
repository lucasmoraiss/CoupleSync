// Regras de tela dos códigos enviados por e-mail (recuperação de senha e confirmação de e-mail).
// Módulo sem dependência de React Native, para ser testável com Jest.
import { getApiErrorCode, getApiErrorMessage } from '@/services/apiError';

export const CODE_LENGTH = 6;

/** Segundos que o app espera antes de deixar pedir outro código (o servidor também limita). */
export const RESEND_COOLDOWN_SECONDS = 30;

export const RESET_UNAVAILABLE_MESSAGE =
  'A recuperação de senha está indisponível no momento, porque o envio de e-mail não está ativo. Tente novamente mais tarde.';
export const VERIFY_UNAVAILABLE_MESSAGE =
  'A confirmação de e-mail está indisponível no momento, porque o envio de e-mail não está ativo. Você pode continuar usando o app normalmente.';

/** Só os 6 dígitos, sem espaços nem letras (quem cola o código do e-mail costuma trazer espaços). */
export function normalizeCode(input: string): string {
  return input.replace(/\D/g, '').slice(0, CODE_LENGTH);
}

export function isCompleteCode(code: string): boolean {
  return new RegExp(`^\\d{${CODE_LENGTH}}$`).test(code);
}

/** O servidor está sem provedor de e-mail configurado (503 EMAIL_NOT_CONFIGURED). */
export function isEmailNotConfigured(error: unknown): boolean {
  return getApiErrorCode(error) === 'EMAIL_NOT_CONFIGURED';
}

/** Mensagem para o usuário: se o e-mail não está configurado, explica que o recurso está indisponível. */
export function getEmailFlowErrorMessage(error: unknown, fallback: string, unavailableMessage: string): string {
  return isEmailNotConfigured(error) ? unavailableMessage : getApiErrorMessage(error, fallback);
}
