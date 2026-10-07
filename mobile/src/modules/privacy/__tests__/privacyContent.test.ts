import fs from 'fs';
import path from 'path';
import {
  AI_CHAT_SECTIONS,
  CAPTURE_APPS,
  CAPTURE_CONSENT_SECTIONS,
  CAPTURE_SOURCES,
  PRIVACY_CONTACT,
  consentStatusLines,
  privacySections,
} from '../privacyContent';
import { EMPTY_CONSENT, type ConsentRecord } from '../consent';
import patterns from '../../integrations/notification-capture/notification-patterns.json';

const flat = (sections: readonly { title: string; paragraphs: readonly string[] }[]) =>
  sections.map((s) => `${s.title}\n${s.paragraphs.join('\n')}`).join('\n');

// A tela Privacidade nas duas situações: com o recurso de IA no app e sem ele (como está hoje em produção).
const WITH_AI = privacySections(true);
const WITHOUT_AI = privacySections(false);

describe('textos de privacidade', () => {
  it('o consentimento da captura diz o que é lido, o que é enviado e que o texto bruto não sai', () => {
    const text = flat(CAPTURE_CONSENT_SECTIONS);
    expect(text).toContain('O que o app lê');
    for (const app of CAPTURE_APPS) expect(text).toContain(app);
    expect(text).toMatch(/banco, o valor, o estabelecimento e a data e hora/);
    expect(text).toMatch(/texto da notificação NÃO é enviado/);
  });

  it('o aviso do chat cita o Google Gemini e os dados enviados', () => {
    const text = flat(AI_CHAT_SECTIONS);
    expect(text).toContain('Google Gemini');
    for (const dado of ['renda', 'orçamento', 'gastos', 'metas']) expect(text).toContain(dado);
  });

  it.each([
    ['com IA', WITH_AI],
    ['sem IA', WITHOUT_AI],
  ])('a tela Privacidade (%s) cobre coleta, armazenamento, compartilhamento (grupo e Gemini) e exclusão', (_name, sections) => {
    const titles = sections.map((s) => s.title);
    expect(titles).toEqual([
      'Dados que o CoupleSync coleta',
      'Onde ficam',
      'Com quem são compartilhados',
      'Como pedir a exclusão',
    ]);
    const text = flat(sections);
    expect(text).toContain('membros do seu grupo');
    expect(text).toContain('Google Gemini');
  });

  it.each([
    ['com IA', WITH_AI],
    ['sem IA', WITHOUT_AI],
  ])('(%s) cita todos os destinatários que o código pode usar, e o Azure só como condicional', (_name, sections) => {
    const text = flat(sections);
    expect(text).toContain('Google Gemini');
    expect(text).toContain('Firebase Cloud Messaging');
    expect(text).toContain('hospedagem');
    expect(text).toMatch(/Dependendo da configuração do servidor.*Azure Document Intelligence/);
  });

  it('não promete imagens: o app envia só PDF', () => {
    expect(flat(WITH_AI)).not.toMatch(/imagens?/i);
    expect(flat(WITHOUT_AI)).not.toMatch(/imagens?/i);
  });

  it('o contato de exclusão vem de uma constante única', () => {
    expect(flat(WITH_AI)).toContain(PRIVACY_CONTACT);
    expect(flat(WITHOUT_AI)).toContain(PRIVACY_CONTACT);
    expect(PRIVACY_CONTACT).not.toMatch(/@/); // nenhum e-mail inventado
  });

  it('o aviso do chat informa que descrições de extratos podem ir ao Gemini após o aceite', () => {
    expect(flat(AI_CHAT_SECTIONS)).toMatch(/descrições das linhas dos extratos/);
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

describe('a tela Privacidade com o recurso de IA desligado no app (M-I3)', () => {
  const accepted: ConsentRecord = {
    ...EMPTY_CONSENT,
    capture: { acceptedAt: '2026-10-01T12:00:00Z', decidedAt: '2026-10-01T12:00:00Z', promptShownAt: null, enabled: true },
    aiChat: { acceptedAt: '2026-10-02T12:00:00Z', declinedAt: null },
  };
  const date = (iso: string) => `[${iso.slice(0, 10)}]`;

  it('sem IA no app, o Gemini aparece como condicional e não como algo já em uso', () => {
    const text = flat(WITHOUT_AI);
    expect(text).toMatch(/apenas quando o recurso de IA estiver disponível no app/);
    expect(text).toMatch(/Nesta versão ele está desligado e nada é enviado ao Gemini/);
    expect(text).not.toMatch(/somente depois que você aceita o aviso do Chat IA/);
  });

  it('com IA no app, o texto é o do aceite do aviso do Chat IA', () => {
    const text = flat(WITH_AI);
    expect(text).toMatch(/somente depois que você aceita o aviso do Chat IA/);
    expect(text).not.toMatch(/Nesta versão ele está desligado/);
  });

  it('sem IA no app, não há linha de "Chat IA: aceito / não aceito" (nem para quem aceitou antes)', () => {
    expect(consentStatusLines(EMPTY_CONSENT, false, date)).toEqual(['Captura de notificações: não aceita.']);
    expect(consentStatusLines(accepted, false, date)).toEqual(['Captura de notificações: aceita em [2026-10-01] (ligada).']);
  });

  it('com IA no app, a resposta do Chat IA aparece', () => {
    expect(consentStatusLines(EMPTY_CONSENT, true, date)).toEqual([
      'Captura de notificações: não aceita.',
      'Chat IA (Google Gemini): não aceito.',
    ]);
    expect(consentStatusLines(accepted, true, date)).toEqual([
      'Captura de notificações: aceita em [2026-10-01] (ligada).',
      'Chat IA (Google Gemini): aceito em [2026-10-02].',
    ]);
  });

  it('captura aceita e depois desligada aparece como desligada', () => {
    const off = { ...accepted, capture: { ...accepted.capture, enabled: false } };
    expect(consentStatusLines(off, false, date)[0]).toBe('Captura de notificações: aceita em [2026-10-01] (desligada).');
  });
});
