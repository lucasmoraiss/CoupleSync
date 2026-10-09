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

  it('não promete que o Client ID nunca é mostrado: a tela de gestão mostra os 4 últimos caracteres ao grupo', () => {
    expect(text).toContain('nunca são mostrados de novo por inteiro');
    expect(text).toContain('só os 4 últimos caracteres do Client ID aparecem, para o grupo reconhecer a conexão');
    expect(text).not.toContain('nem para você, nem para o seu grupo');
  });

  it('avisa que sair do grupo, ou ser removido dele, apaga a conexão, os bancos e as contas da pessoa naquele grupo (issue #31)', () => {
    expect(text).toContain(
      'Se você sair do grupo, ou for removido dele, a sua conexão, os seus bancos e as suas contas são apagados daquele grupo.',
    );
  });

  it('conectar um banco não liga a IA: o aceite dela é outro, em Configurações > Inteligência artificial', () => {
    expect(text).toContain('Conectar um banco não liga a IA nem muda o aceite dela.');
    expect(text).toContain('A análise com IA tem aceite próprio; veja Configurações > Inteligência artificial.');
    expect(text).not.toMatch(/Chat IA/);
  });

  it('diz que as transações do banco são copiadas para o servidor a cada sincronização, e que nada entra sem revisão (issue #25)', () => {
    expect(text).toContain('o servidor guarda uma cópia das transações desses bancos');
    expect(text).toMatch(/3, 6 ou 12 meses/);
    expect(text).toMatch(/pede a sincronização sozinho ao ser aberto/);
    expect(text).toContain('Nada entra nas suas finanças sem alguém do grupo revisar e confirmar.');
    expect(text).not.toMatch(/próxima atualização/);
    expect(text).not.toContain('só confere a conexão');
  });

  it('diz o que acontece com as transações do banco ao desconectar e ao sair do grupo (issue #25)', () => {
    expect(text).toContain('as transações já trazidas continuam na revisão');
    expect(text).toContain('As transações do banco que ainda estavam na revisão são apagadas junto. As despesas que já tinham sido confirmadas ficam no grupo.');
  });

  it('diz o que das despesas do banco vai à IA: nada sem a análise ativada para o grupo; com ela, só as confirmadas, nos totais por categoria (issues #25 e #38)', () => {
    expect(text).toContain('Sem a análise com IA ativada para o grupo, nada do que vier do banco é enviado à IA.');
    expect(text).toContain(
      'Com ela ativada, as despesas do banco que alguém do grupo confirmou na revisão entram, como qualquer outra despesa, nos resumos calculados das finanças do grupo (totais por categoria).',
    );
  });

  it('não promete o envio da descrição de uma despesa do banco para categorizar: a categorização por IA está desligada no servidor (issue #38)', () => {
    expect(text).not.toMatch(/sugerir a categoria/);
    expect(text).not.toMatch(/Gemini/);
  });
});

describe('tela Privacidade', () => {
  it('cita o Pluggy na coleta e no compartilhamento', () => {
    const sections = privacySections();
    const collected = sections.find((s) => s.title === 'Dados que o CoupleSync coleta')!.paragraphs.join('\n');
    const shared = sections.find((s) => s.title === 'Com quem são compartilhados')!.paragraphs.join('\n');

    expect(collected).toMatch(/Open Finance \(só se você conectar\)/);
    expect(collected).toMatch(/cifrad/);
    expect(shared).toMatch(/Pluggy/);
    expect(shared).toMatch(/membros do seu grupo/);
  });

  it('o aceite do Open Finance aparece em "Suas respostas" só para quem aceitou', () => {
    const date = (iso: string) => `[${iso.slice(0, 10)}]`;

    expect(consentStatusLines(EMPTY_CONSENT, date)).toEqual(['Captura de notificações: não aceita.']);
    expect(consentStatusLines(acceptOpenFinance(EMPTY_CONSENT, NOW), date)).toEqual([
      'Captura de notificações: não aceita.',
      'Open Finance (Meu Pluggy): aceito em [2026-10-07].',
    ]);
  });
});
