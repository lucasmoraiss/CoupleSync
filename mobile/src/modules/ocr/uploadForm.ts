// Corpo do envio do extrato. O campo "aiCategorizationConsent" diz ao servidor se quem enviou aceitou o aviso
// de IA (Google Gemini); só então as descrições das linhas podem ir ao Gemini para sugerir categorias.
// Sem o campo (ou "false") o servidor não usa IA nessa importação.
export interface FormDataLike {
  append(name: string, value: any): void;
}

export const AI_CONSENT_FIELD = 'aiCategorizationConsent';

export function appendAiConsent<T extends FormDataLike>(form: T, aiChatAccepted: boolean): T {
  form.append(AI_CONSENT_FIELD, aiChatAccepted ? 'true' : 'false');
  return form;
}

/**
 * O consentimento de IA que vai junto com o extrato. Com o recurso de IA desligado no app o valor é sempre
 * falso, mesmo que haja um aceite antigo guardado: o usuário não tem hoje onde ver nem rever esse aceite.
 */
export function aiConsentForUpload(aiFeatureEnabled: boolean, aiChatAccepted: boolean): boolean {
  return aiFeatureEnabled && aiChatAccepted;
}

export type UploadPhase = 'idle' | 'uploading' | 'polling' | 'error';

/**
 * A tela de envio continua montada entre visitas. Um erro de uma tentativa anterior não deve estar na tela ao
 * voltar; um envio ou processamento em andamento continua (sair da tela não cancela a importação).
 */
export function shouldResetUploadOnRevisit(phase: UploadPhase): boolean {
  return phase === 'error';
}
