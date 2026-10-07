// Open Finance (issue #24): aceite de privacidade específico do wizard e o que a tela Privacidade diz sobre ele.
import {
  EMPTY_CONSENT,
  acceptAiChat,
  acceptCapture,
  acceptOpenFinance,
  isAiChatAllowed,
  isCaptureAllowed,
  isOpenFinanceAccepted,
  parseConsent,
  serializeConsent,
} from '../consent';
import {
  OPEN_FINANCE_CONSENT_SECTIONS,
  OPEN_FINANCE_CONSENT_TITLE,
  consentStatusLines,
  privacySections,
} from '../privacyContent';

const NOW = '2026-10-07T12:00:00.000Z';
const flat = (sections: readonly { title: string; paragraphs: readonly string[] }[]) =>
  sections.map((s) => `${s.title}\n${s.paragraphs.join('\n')}`).join('\n');

describe('aceite do Open Finance', () => {
  it('sem resposta não há aceite; aceitar registra a data', () => {
    expect(isOpenFinanceAccepted(EMPTY_CONSENT)).toBe(false);

    const accepted = acceptOpenFinance(EMPTY_CONSENT, NOW);

    expect(isOpenFinanceAccepted(accepted)).toBe(true);
    expect(accepted.openFinance.acceptedAt).toBe(NOW);
  });

  it('é independente da captura e do Chat IA', () => {
    const accepted = acceptOpenFinance(EMPTY_CONSENT, NOW);
    expect(isCaptureAllowed(accepted)).toBe(false);
    expect(isAiChatAllowed(accepted)).toBe(false);
    expect(isOpenFinanceAccepted(acceptAiChat(acceptCapture(EMPTY_CONSENT, NOW), NOW))).toBe(false);
  });

  it('ida e volta preserva o aceite', () => {
    const record = acceptOpenFinance(acceptCapture(EMPTY_CONSENT, NOW), NOW);
    expect(parseConsent(serializeConsent(record))).toEqual(record);
  });

  it('registro guardado por uma versão anterior do app (sem Open Finance) continua valendo, sem aceite do Open Finance', () => {
    const old = JSON.stringify({
      version: 1,
      capture: { acceptedAt: NOW, decidedAt: NOW, promptShownAt: null, enabled: true },
      aiChat: { acceptedAt: NOW, declinedAt: null },
    });

    const record = parseConsent(old);

    expect(isCaptureAllowed(record)).toBe(true);
    expect(isAiChatAllowed(record)).toBe(true);
    expect(isOpenFinanceAccepted(record)).toBe(false);
  });

  it('data inválida não vale como aceite', () => {
    const forged = JSON.stringify({ version: 1, capture: {}, aiChat: {}, openFinance: { acceptedAt: 'ontem' } });
    expect(isOpenFinanceAccepted(parseConsent(forged))).toBe(false);
  });
});

describe('texto do passo "O que é"', () => {
  const text = flat(OPEN_FINANCE_CONSENT_SECTIONS);

  it('tem título e explica o Meu Pluggy: gratuito, regulado pelo Banco Central, só os seus dados', () => {
    expect(OPEN_FINANCE_CONSENT_TITLE).toMatch(/Open Finance/);
    expect(text).toContain('Meu Pluggy');
    expect(text).toMatch(/gratuito/);
    expect(text).toMatch(/regulado pelo Banco Central/);
    expect(text).toMatch(/só os seus (próprios )?dados/);
  });

  it('diz o que o app passa a ver: extrato, cartão, saldos e investimentos', () => {
    for (const dado of ['extrato', 'cartão', 'saldos', 'investimentos']) expect(text).toContain(dado);
  });

  it('avisa que o grupo vê tudo e que só quem conectou altera ou desconecta', () => {
    expect(text).toMatch(/membros do (seu )?grupo veem/);
    expect(text).toMatch(/[Ss]ó você/);
    expect(text).toMatch(/desconectar/);
  });

  it('diz como as credenciais são guardadas e que o segredo não volta para o app', () => {
    expect(text).toMatch(/cifrad/);
    expect(text).toContain('Client ID');
    expect(text).toContain('Client Secret');
  });

  it('a IA segue o consentimento que já existe: nada de novo é enviado ao Gemini por causa do Open Finance', () => {
    expect(text).toMatch(/Chat IA/);
    expect(text).toMatch(/aviso do Chat IA/);
  });

  it('nesta versão nenhuma transação é trazida do banco', () => {
    expect(text).toMatch(/próxima atualização/);
  });
});

describe('tela Privacidade', () => {
  it.each([true, false])('cita o Pluggy na coleta e no compartilhamento (IA no app: %p)', (aiAvailable) => {
    const sections = privacySections(aiAvailable);
    const collected = sections.find((s) => s.title === 'Dados que o CoupleSync coleta')!.paragraphs.join('\n');
    const shared = sections.find((s) => s.title === 'Com quem são compartilhados')!.paragraphs.join('\n');

    expect(collected).toMatch(/Open Finance \(só se você conectar\)/);
    expect(collected).toMatch(/cifrad/);
    expect(shared).toMatch(/Pluggy/);
    expect(shared).toMatch(/membros do seu grupo/);
  });

  it('o aceite do Open Finance aparece em "Suas respostas" só para quem aceitou', () => {
    const date = (iso: string) => `[${iso.slice(0, 10)}]`;

    expect(consentStatusLines(EMPTY_CONSENT, false, date)).toEqual(['Captura de notificações: não aceita.']);
    expect(consentStatusLines(acceptOpenFinance(EMPTY_CONSENT, NOW), false, date)).toEqual([
      'Captura de notificações: não aceita.',
      'Open Finance (Meu Pluggy): aceito em [2026-10-07].',
    ]);
  });
});
