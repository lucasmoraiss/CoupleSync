import { appendAiConsent, shouldResetUploadOnRevisit } from '../uploadForm';

function fakeForm() {
  const fields: Record<string, unknown> = {};
  return { fields, append: (name: string, value: unknown) => { fields[name] = value; } };
}

describe('appendAiConsent', () => {
  it('envia true só quando a análise com IA está ativada para o grupo', () => {
    const accepted = fakeForm();
    appendAiConsent(accepted, true);
    expect(accepted.fields.aiCategorizationConsent).toBe('true');
  });

  it('envia false quando não está ativada', () => {
    const declined = fakeForm();
    appendAiConsent(declined, false);
    expect(declined.fields.aiCategorizationConsent).toBe('false');
  });
});

describe('shouldResetUploadOnRevisit (M-I1)', () => {
  it('o erro de uma tentativa anterior não fica na tela ao voltar', () => {
    expect(shouldResetUploadOnRevisit('error')).toBe(true);
  });

  it('envio e processamento em andamento continuam; tela parada não muda', () => {
    expect(shouldResetUploadOnRevisit('uploading')).toBe(false);
    expect(shouldResetUploadOnRevisit('polling')).toBe(false);
    expect(shouldResetUploadOnRevisit('idle')).toBe(false);
  });
});
