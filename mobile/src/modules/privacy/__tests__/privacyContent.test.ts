import fs from 'fs';
import path from 'path';
import {
  AI_CHAT_SECTIONS,
  CAPTURE_APPS,
  CAPTURE_CONSENT_SECTIONS,
  PRIVACY_SECTIONS,
} from '../privacyContent';

const flat = (sections: readonly { title: string; paragraphs: readonly string[] }[]) =>
  sections.map((s) => `${s.title}\n${s.paragraphs.join('\n')}`).join('\n');

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

  it('a tela Privacidade cobre coleta, armazenamento, compartilhamento (grupo e Gemini) e exclusão', () => {
    const titles = PRIVACY_SECTIONS.map((s) => s.title);
    expect(titles).toEqual([
      'Dados que o CoupleSync coleta',
      'Onde ficam',
      'Com quem são compartilhados',
      'Como pedir a exclusão',
    ]);
    const text = flat(PRIVACY_SECTIONS);
    expect(text).toContain('membros do seu grupo');
    expect(text).toContain('Google Gemini');
  });

  it('os apps listados são os que o serviço nativo realmente captura', () => {
    const kt = fs.readFileSync(
      path.join(__dirname, '../../../../android-native/java/com/couplesync/app/NotificationCaptureService.kt'),
      'utf8',
    );
    const packages = Array.from(kt.matchAll(/"((?:com|br)\.[a-z0-9.]+)"/g)).map((m) => m[1]);
    const byApp: Record<string, string[]> = {
      Nubank: ['com.nu.production'],
      'Itaú': ['com.itau', 'br.com.italiquido', 'com.itau.empresas'],
      Inter: ['br.com.intermedium'],
      'C6 Bank': ['com.c6bank.app'],
      Bradesco: ['com.bradesco', 'com.bradesco.prestoandroid', 'com.bradesco.next'],
    };
    expect(Object.keys(byApp).sort()).toEqual([...CAPTURE_APPS].sort());
    expect(packages.sort()).toEqual(Object.values(byApp).flat().sort());
  });
});
