import fs from 'fs';
import path from 'path';
import {
  AI_ANALYSIS_TITLE,
  AI_KNOWN_DESTINATIONS,
  aiAnalysisPoints,
  aiAnalysisSections,
  aiAnalysisSummary,
  AI_OWN_CONSENT_NOTE,
  CAPTURE_APPS,
  CAPTURE_CONSENT_SECTIONS,
  CAPTURE_SOURCES,
  PRIVACY_CONTACT,
  consentStatusLines,
  privacySections,
} from '../privacyContent';
import { CONSENT_VERSION, EMPTY_CONSENT, isCaptureAllowed, isOpenFinanceAccepted, parseConsent, type ConsentRecord } from '../consent';
import patterns from '../../integrations/notification-capture/notification-patterns.json';

const flat = (sections: readonly { title: string; paragraphs: readonly string[] }[]) =>
  sections.map((s) => `${s.title}\n${s.paragraphs.join('\n')}`).join('\n');

// O texto geral da tela Privacidade (a análise com IA tem seção própria, testada mais abaixo).
const GENERAL = privacySections();

describe('textos de privacidade', () => {
  it('o consentimento da captura diz o que é lido, o que é enviado e que o texto bruto não sai', () => {
    const text = flat(CAPTURE_CONSENT_SECTIONS);
    expect(text).toContain('O que o app lê');
    for (const app of CAPTURE_APPS) expect(text).toContain(app);
    expect(text).toMatch(/banco, o valor, o estabelecimento e a data e hora/);
    expect(text).toMatch(/texto da notificação NÃO é enviado/);
  });

  it('a tela Privacidade cobre coleta, armazenamento, compartilhamento e exclusão', () => {
    expect(GENERAL.map((s) => s.title)).toEqual([
      'Dados que o CoupleSync coleta',
      'Onde ficam',
      'Com quem são compartilhados',
      'Como pedir a exclusão',
    ]);
    expect(flat(GENERAL)).toContain('membros do seu grupo');
  });

  it('cita os destinatários que o código pode usar, e o Azure só como condicional', () => {
    const text = flat(GENERAL);
    expect(text).toContain('Firebase Cloud Messaging');
    expect(text).toContain('hospedagem');
    expect(text).toMatch(/Dependendo da configuração do servidor.*Azure Document Intelligence/);
  });

  it('não promete imagens: o app envia só PDF', () => {
    expect(flat(GENERAL)).not.toMatch(/imagens?/i);
  });

  it('o contato de exclusão vem de uma constante única', () => {
    expect(flat(GENERAL)).toContain(PRIVACY_CONTACT);
    expect(PRIVACY_CONTACT).toBe('salomaolucas13@outlook.com');
    const deletion = GENERAL.find((section) => section.title === 'Como pedir a exclusão');
    expect(deletion?.paragraphs.join(' ')).toContain(`escreva para ${PRIVACY_CONTACT}`);
  });
});

describe('o texto lista exatamente o que a captura lê (M-M7)', () => {
  const kotlin = fs.readFileSync(
    path.join(__dirname, '../../../../android-native/java/com/couplesync/app/NotificationCaptureService.kt'),
    'utf8',
  );
  // Só o conteúdo de SUPPORTED_PACKAGES = setOf( ... ): nenhum outro texto do arquivo entra na conta.
  const block = /SUPPORTED_PACKAGES\s*=\s*setOf\(([^)]*)\)/.exec(kotlin);
  const nativePackages = block ? Array.from(block[1].matchAll(/"([^"]+)"/g)).map((m) => m[1]) : [];
  const listedPackages = CAPTURE_SOURCES.flatMap((source) => source.packages);
  const consentText = flat(CAPTURE_CONSENT_SECTIONS);

  it('a lista do serviço nativo foi lida do arquivo Kotlin', () => {
    expect(block).not.toBeNull();
    expect(nativePackages.length).toBeGreaterThanOrEqual(5);
  });

  it('os pacotes do texto são exatamente os do serviço nativo: nenhum a mais, nenhum a menos, nenhum repetido', () => {
    expect([...listedPackages].sort()).toEqual([...nativePackages].sort());
    expect(new Set(listedPackages).size).toBe(listedPackages.length);
  });

  it('banco a banco, a lista é a mesma que o app usa para interpretar as notificações', () => {
    const fromPatterns = (patterns as { banks: { name: string; packageNames: string[] }[] }).banks.map((bank) => ({
      bank: bank.name,
      packages: bank.packageNames,
    }));
    expect(CAPTURE_SOURCES).toEqual(fromPatterns);
  });

  it('o consentimento mostra cada banco e CADA aplicativo lido (pelo identificador)', () => {
    for (const source of CAPTURE_SOURCES) {
      expect(consentText).toContain(source.bank);
      for (const packageName of source.packages) expect(consentText).toContain(packageName);
    }
  });

  it('o texto avisa que bancos com mais de um aplicativo têm os outros aplicativos lidos também', () => {
    expect(consentText).toMatch(/incluindo os outros aplicativos do Itaú e Bradesco/);
  });

  it('o texto não cita identificador de aplicativo que a captura não lê', () => {
    const mentioned = Array.from(consentText.matchAll(/\b(?:com|br)\.[a-z0-9]+(?:\.[a-z0-9]+)*/g)).map((m) => m[0]);
    expect([...mentioned].sort()).toEqual([...nativePackages].sort());
  });
});

describe('"Suas respostas neste aparelho"', () => {
  const accepted: ConsentRecord = {
    ...EMPTY_CONSENT,
    capture: { acceptedAt: '2026-10-01T12:00:00Z', decidedAt: '2026-10-01T12:00:00Z', promptShownAt: null, enabled: true },
    aiChat: { acceptedAt: '2026-10-02T12:00:00Z', declinedAt: null },
  };
  const date = (iso: string) => `[${iso.slice(0, 10)}]`;

  it('não há linha de IA, nem para quem tinha aceitado o chat antigo: o aceite de IA é do grupo e fica no servidor', () => {
    expect(consentStatusLines(EMPTY_CONSENT, date)).toEqual(['Captura de notificações: não aceita.']);
    expect(consentStatusLines(accepted, date)).toEqual(['Captura de notificações: aceita em [2026-10-01] (ligada).']);
  });

  it('captura aceita e depois desligada aparece como desligada', () => {
    const off = { ...accepted, capture: { ...accepted.capture, enabled: false } };
    expect(consentStatusLines(off, date)[0]).toBe('Captura de notificações: aceita em [2026-10-01] (desligada).');
  });
});

// Os destinos como GET /ai/status os devolve.
const GOOGLE = { name: 'Google (Gemini)', country: 'Estados Unidos', trainsOnData: true };
const GROQ = { name: 'Groq', country: 'Estados Unidos', trainsOnData: false };

describe('seção "Análise com IA" (issue #38)', () => {
  it('só com o Google ligado, tem exatamente os sete pontos do texto aprovado, nesta ordem', () => {
    expect(AI_ANALYSIS_TITLE).toBe('Análise com IA');
    expect(aiAnalysisPoints([GOOGLE])).toEqual([
      'O que vai: resumos calculados das finanças do grupo (totais por categoria, lojas, assinaturas, parcelas, valores das metas, tipos de renda), nomes de lojas para categorizar e as perguntas feitas ao Assistente, como foram escritas.',
      'O que nunca vai: nomes, e-mails e CPF de vocês. O que o app não envia por conta própria: nomes das metas, nomes das rendas, números de conta ou cartão e nomes de quem recebeu ou enviou transferências (elas vão só como "transferência"). O que você escreve numa pergunta é enviado como você escreveu: se citar o nome de uma meta ou de quem recebeu uma transferência, ele vai junto. Nomes de vocês, CPF, telefone, e-mail e chave Pix digitados numa pergunta são retirados antes do envio. Nomes de lojas vão como aparecem no extrato.',
      'Para onde: Google (Gemini), nos Estados Unidos — transferência internacional de dados.',
      'Com franqueza: "No plano gratuito, o Google pode usar o conteúdo enviado para melhorar os produtos dele e revisores humanos podem lê-lo."',
      'Quem ativa liga a análise para o grupo inteiro; o outro membro é avisado no app e pode desligar a qualquer hora em Configurações; desligar não apaga o histórico, que pode ser apagado à parte.',
      'O resumo semanal por e-mail é opcional, por pessoa, enviado pela Brevo (o serviço de e-mail que o app já usa).',
      'Os cálculos (assinaturas, parcelas, previsão) são feitos no próprio servidor do app e funcionam sem a IA.',
    ]);
  });

  it('é uma seção só, separada do texto geral, com os sete pontos', () => {
    expect(aiAnalysisSections([GOOGLE])).toEqual([{ title: 'Análise com IA', paragraphs: aiAnalysisPoints([GOOGLE]) }]);
    expect(aiAnalysisSections([GOOGLE, GROQ])).toEqual([{ title: 'Análise com IA', paragraphs: aiAnalysisPoints([GOOGLE, GROQ]) }]);
    expect(GENERAL.map((s) => s.title)).not.toContain('Análise com IA');
  });

  it('no texto geral a IA só aparece como "tem aceite próprio": nenhum parágrafo antigo sobre o Chat IA ou o Gemini sobra', () => {
    const text = flat(GENERAL);
    expect(AI_OWN_CONSENT_NOTE).toBe('A análise com IA tem aceite próprio; veja Configurações > Inteligência artificial.');
    expect(text).toContain(AI_OWN_CONSENT_NOTE);
    expect(text).not.toMatch(/Chat IA/);
    expect(text).not.toMatch(/Gemini/);
    expect(text).not.toMatch(/Nesta versão ele está desligado/);
  });

  it('a linha da tela de boas-vindas diz o que vai e para onde, sem contradizer os sete pontos', () => {
    expect(aiAnalysisSummary([GOOGLE])).toBe(
      'Vão para o Google (Gemini), nos Estados Unidos, resumos das finanças do grupo e as suas perguntas — nunca nomes, e-mails ou CPF de vocês.',
    );
  });

  describe('segundo provedor (Groq): o texto cita os destinos que o servidor tem ligados', () => {
    const WHERE = 2;
    const FRANK = 3;

    it('com o Groq ligado, "Para onde" e "Com franqueza" citam os dois; os outros cinco pontos não mudam', () => {
      const one = aiAnalysisPoints([GOOGLE]);
      const two = aiAnalysisPoints([GOOGLE, GROQ]);

      expect(two).toHaveLength(7);
      expect(two[WHERE]).toBe('Para onde: Google (Gemini) e Groq, nos Estados Unidos — transferência internacional de dados.');
      expect(two[FRANK]).toBe(
        'Com franqueza: "No plano gratuito, o Google pode usar o conteúdo enviado para melhorar os produtos dele e revisores humanos podem lê-lo. ' +
          'O Groq declara, nos termos de uso dele, que não usa o conteúdo enviado para treinar modelos; ele pode guardar pedidos e respostas por até 30 dias para investigar abuso ou falhas."',
      );
      for (const index of [0, 1, 4, 5, 6]) expect(two[index]).toBe(one[index]);
    });

    it('sem o Groq ligado, o Groq não aparece em lugar nenhum do texto', () => {
      expect(aiAnalysisPoints([GOOGLE]).join('\n')).not.toMatch(/Groq/);
      expect(aiAnalysisSummary([GOOGLE])).not.toMatch(/Groq/);
    });

    it('a linha da tela de boas-vindas cita os dois quando os dois estão ligados', () => {
      expect(aiAnalysisSummary([GOOGLE, GROQ])).toBe(
        'Vão para o Google (Gemini) e para o Groq, nos Estados Unidos, resumos das finanças do grupo e as suas perguntas — nunca nomes, e-mails ou CPF de vocês.',
      );
    });

    it('sem saber o que o servidor tem ligado, o texto cita todos os destinos que o aceite cobre — nunca menos', () => {
      expect(AI_KNOWN_DESTINATIONS).toEqual([GOOGLE, GROQ]);
      for (const unknown of [null, undefined, []] as const) {
        expect(aiAnalysisPoints(unknown)).toEqual(aiAnalysisPoints([GOOGLE, GROQ]));
        expect(aiAnalysisSummary(unknown)).toBe(aiAnalysisSummary([GOOGLE, GROQ]));
      }
    });

    it('um destino que o app não conhece pelo nome é descrito pelo que o servidor informa, com o país', () => {
      const other = { name: 'Outro', country: 'França', trainsOnData: true };
      const points = aiAnalysisPoints([GOOGLE, other]);
      expect(points[WHERE]).toBe('Para onde: Google (Gemini) (nos Estados Unidos) e Outro (em França) — transferência internacional de dados.');
      expect(points[FRANK]).toContain('Outro pode usar o conteúdo enviado para melhorar os produtos dele.');
      expect(aiAnalysisPoints([{ ...other, trainsOnData: false }])[FRANK]).toBe(
        'Com franqueza: "Outro declara que não usa o conteúdo enviado para treinar modelos."',
      );
    });
  });

  it('a tela Privacidade mostra a seção em destaque, separada do texto geral, e leva às Configurações de IA', () => {
    const screen = fs.readFileSync(path.join(__dirname, '../../../../app/(main)/settings/privacy.tsx'), 'utf8');
    expect(screen).toContain('aiAnalysisSections(aiDestinations(status))');
    expect(screen).toContain('styles.aiBox');
    expect(screen).toMatch(/router\.push\('\/\(main\)\/settings\/ai'/);
    expect(screen).not.toContain('aiAvailability');
  });
});

describe('a mudança de texto não mexe nos aceites já dados no aparelho (issue #38)', () => {
  it('CONSENT_VERSION continua 1: subir apagaria os aceites de captura e de Open Finance de todo mundo', () => {
    expect(CONSENT_VERSION).toBe(1);
    expect(EMPTY_CONSENT.version).toBe(1);
  });

  it('um registro gravado antes desta versão, com captura e Open Finance aceitos, continua lido como aceito', () => {
    // Exatamente o que o app instalado guarda hoje no armazenamento seguro (inclusive o campo aiChat, que fica).
    const stored = JSON.stringify({
      version: 1,
      capture: { acceptedAt: '2026-10-01T12:00:00.000Z', decidedAt: '2026-10-01T12:00:00.000Z', promptShownAt: null, enabled: true },
      aiChat: { acceptedAt: '2026-10-02T12:00:00.000Z', declinedAt: null },
      openFinance: { acceptedAt: '2026-10-07T12:00:00.000Z' },
    });

    const record = parseConsent(stored);

    expect(isCaptureAllowed(record)).toBe(true);
    expect(record.capture.acceptedAt).toBe('2026-10-01T12:00:00.000Z');
    expect(isOpenFinanceAccepted(record)).toBe(true);
    expect(record.openFinance.acceptedAt).toBe('2026-10-07T12:00:00.000Z');
    // O campo antigo do chat não é apagado nem migrado: só deixou de ser consultado.
    expect(record.aiChat.acceptedAt).toBe('2026-10-02T12:00:00.000Z');
    expect(consentStatusLines(record, (iso) => iso.slice(0, 10))).toEqual([
      'Captura de notificações: aceita em 2026-10-01 (ligada).',
      'Open Finance (Meu Pluggy): aceito em 2026-10-07.',
    ]);
  });

  it('um registro de outra versão seria lido como vazio — é por isso que a versão não sobe', () => {
    const other = JSON.stringify({
      version: 2,
      capture: { acceptedAt: '2026-10-01T12:00:00.000Z', enabled: true },
      openFinance: { acceptedAt: '2026-10-07T12:00:00.000Z' },
    });
    expect(parseConsent(other)).toEqual(EMPTY_CONSENT);
  });
});
