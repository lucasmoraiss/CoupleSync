import { captureBannerText, captureToggleAction, describeCaptureStatus } from '../captureStatus';
import type { CaptureConsent } from '../consent';

const date = (iso: string) => `[${iso.slice(0, 10)}]`;
const NEVER: CaptureConsent = { acceptedAt: null, decidedAt: null, promptShownAt: null, enabled: false };
const ON: CaptureConsent = { acceptedAt: '2026-10-01T12:00:00Z', decidedAt: '2026-10-01T12:00:00Z', promptShownAt: null, enabled: true };
const OFF: CaptureConsent = { ...ON, enabled: false };

describe('estado da captura nas Configurações (M-M11)', () => {
  it('o defeito: aceita e ligada, mas sem a permissão do Android, NÃO aparece como ligada e diz o que falta', () => {
    const status = describeCaptureStatus(ON, false, date);

    expect(status.switchOn).toBe(false);
    expect(status.missing).toBe('permission');
    expect(status.description).not.toMatch(/Ligada\./);
    expect(status.description).toMatch(/falta permitir o "Acesso às notificações"/);
    expect(status.description).toContain('[2026-10-01]');
  });

  it('aceita, ligada e com a permissão: ligada', () => {
    expect(describeCaptureStatus(ON, true, date)).toEqual({
      switchOn: true,
      description: 'Aceita em [2026-10-01]. Ligada.',
      missing: null,
    });
  });

  it('permissão desconhecida (ainda consultando, ou aparelho sem o módulo): não afirma que falta', () => {
    expect(describeCaptureStatus(ON, null, date)).toMatchObject({ switchOn: true, missing: null });
  });

  it('aceita e desligada pelo interruptor', () => {
    expect(describeCaptureStatus(OFF, true, date)).toEqual({
      switchOn: false,
      description: 'Aceita em [2026-10-01]. Desligada: nada é enviado.',
      missing: 'switch',
    });
    // Sem permissão o que falta primeiro continua sendo o interruptor.
    expect(describeCaptureStatus(OFF, false, date).missing).toBe('switch');
  });

  it('nunca aceitou: falta o aceite, com ou sem permissão', () => {
    for (const permission of [true, false, null]) {
      expect(describeCaptureStatus(NEVER, permission, date)).toMatchObject({ switchOn: false, missing: 'consent' });
    }
  });
});

describe('aviso na tela de Transações', () => {
  it('só aparece quando o Android diz que a permissão não está dada', () => {
    expect(captureBannerText(ON, true)).toBeNull();
    expect(captureBannerText(ON, null)).toBeNull();
    expect(captureBannerText(NEVER, true)).toBeNull();
  });

  it('diz que falta a permissão para quem já aceitou e ligou; para os demais, que está desativada', () => {
    expect(captureBannerText(ON, false)).toBe('Captura ligada, mas falta a permissão do Android');
    expect(captureBannerText(OFF, false)).toBe('Captura de notificações desativada');
    expect(captureBannerText(NEVER, false)).toBe('Captura de notificações desativada');
  });
});

describe('toque no interruptor', () => {
  it('desligar sempre desliga', () => {
    expect(captureToggleAction(false, ON, true)).toBe('disable');
    expect(captureToggleAction(false, ON, false)).toBe('disable');
  });

  it('ligar sem aceite abre o consentimento', () => {
    expect(captureToggleAction(true, NEVER, false)).toBe('open-consent');
    expect(captureToggleAction(true, NEVER, true)).toBe('open-consent');
  });

  it('já ligada no app e só falta a permissão: abre a tela do Android', () => {
    expect(captureToggleAction(true, ON, false)).toBe('open-system-settings');
  });

  it('desligada no app: liga; se também falta a permissão, abre a tela do Android depois', () => {
    expect(captureToggleAction(true, OFF, true)).toBe('enable');
    expect(captureToggleAction(true, OFF, null)).toBe('enable');
    expect(captureToggleAction(true, OFF, false)).toBe('enable-then-system-settings');
  });
});
