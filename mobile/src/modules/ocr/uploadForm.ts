// Corpo do envio do extrato. O campo "aiCategorizationConsent" diz ao servidor se, para o app, a análise com IA
// está ativada para o grupo (modules/ai/aiStatus.ts: aiUploadConsent). Sem o campo (ou "false") o servidor não
// usa IA nessa importação; com "true" ele ainda confere a ativação do grupo antes de processar.
export interface FormDataLike {
  append(name: string, value: any): void;
}

export const AI_CONSENT_FIELD = 'aiCategorizationConsent';

export function appendAiConsent<T extends FormDataLike>(form: T, aiEnabledForGroup: boolean): T {
  form.append(AI_CONSENT_FIELD, aiEnabledForGroup ? 'true' : 'false');
  return form;
}

export type UploadPhase = 'idle' | 'uploading' | 'polling' | 'error';

/**
 * A tela de envio continua montada entre visitas. Um erro de uma tentativa anterior não deve estar na tela ao
 * voltar; um envio ou processamento em andamento continua (sair da tela não cancela a importação).
 */
export function shouldResetUploadOnRevisit(phase: UploadPhase): boolean {
  return phase === 'error';
}
