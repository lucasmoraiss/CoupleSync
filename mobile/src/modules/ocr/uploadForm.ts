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
