import { appendAiConsent } from '../uploadForm';

function fakeForm() {
  const fields: Record<string, unknown> = {};
  return { fields, append: (name: string, value: unknown) => { fields[name] = value; } };
}

describe('appendAiConsent', () => {
  it('envia true só quando o aviso de IA foi aceito', () => {
    const accepted = fakeForm();
    appendAiConsent(accepted, true);
    expect(accepted.fields.aiCategorizationConsent).toBe('true');
  });

  it('envia false quando não aceitou', () => {
    const declined = fakeForm();
    appendAiConsent(declined, false);
    expect(declined.fields.aiCategorizationConsent).toBe('false');
  });
});
