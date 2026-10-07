import { decideCaptureSync, type CaptureSyncInput } from '../captureSync';
import { EMPTY_CONSENT, acceptCapture, declineCapture, markCapturePromptShown, setCaptureEnabled } from '@/modules/privacy/consent';

const NOW = '2026-10-06T12:00:00.000Z';

function input(overrides: Partial<CaptureSyncInput> = {}): CaptureSyncInput {
  return {
    sessionUserId: 'user-1',
    consentUserId: 'user-1',
    loaded: true,
    record: EMPTY_CONSENT,
    listenerPermissionGranted: true,
    ...overrides,
  };
}

describe('decideCaptureSync: nativo e escuta', () => {
  it('antes de o consentimento carregar deixa o nativo em paz (nunca grava false por engano)', () => {
    const d = decideCaptureSync(input({ loaded: false, consentUserId: null }));
    expect(d).toMatchObject({ native: 'leave', listen: false, clearPending: false, prompt: false });
  });

  it('registro carregado de OUTRO usuário também não conta: deixa em paz', () => {
    const d = decideCaptureSync(input({ consentUserId: 'user-0', record: acceptCapture(EMPTY_CONSENT, NOW) }));
    expect(d.native).toBe('leave');
    expect(d.listen).toBe(false);
  });

  it('usuário que já aceitou e está ligado: habilita e escuta, sem passar por desabilitar', () => {
    const d = decideCaptureSync(input({ record: acceptCapture(EMPTY_CONSENT, NOW) }));
    expect(d).toMatchObject({ native: 'enable', listen: true, clearPending: false });
  });

  it('sem aceite, com interruptor desligado ou recusado: desabilita e descarta pendências', () => {
    const accepted = acceptCapture(EMPTY_CONSENT, NOW);
    for (const record of [EMPTY_CONSENT, setCaptureEnabled(accepted, false, NOW), declineCapture(EMPTY_CONSENT, NOW)]) {
      expect(decideCaptureSync(input({ record }))).toMatchObject({ native: 'disable', listen: false, clearPending: true });
    }
  });

  it('sem sessão (logout/expiração): termina com o nativo desabilitado', () => {
    const d = decideCaptureSync(input({ sessionUserId: null, consentUserId: null, loaded: false }));
    expect(d).toMatchObject({ native: 'disable', listen: false, clearPending: true, prompt: false });
  });
});

describe('decideCaptureSync: tela de consentimento', () => {
  it('abre uma vez para quem já tem a permissão e nunca respondeu', () => {
    expect(decideCaptureSync(input()).prompt).toBe(true);
  });

  it('não abre sem a permissão, ou enquanto ela ainda não foi consultada', () => {
    expect(decideCaptureSync(input({ listenerPermissionGranted: false })).prompt).toBe(false);
    expect(decideCaptureSync(input({ listenerPermissionGranted: null })).prompt).toBe(false);
  });

  it('se a tela já foi mostrada e o usuário saiu sem responder, não reabre (fica nas Configurações)', () => {
    const shown = markCapturePromptShown(EMPTY_CONSENT, NOW);
    const d = decideCaptureSync(input({ record: shown }));
    expect(d.prompt).toBe(false);
    expect(d.native).toBe('disable'); // continua desligada até aceitar
  });

  it('não abre depois de responder, nem antes de carregar', () => {
    expect(decideCaptureSync(input({ record: declineCapture(EMPTY_CONSENT, NOW) })).prompt).toBe(false);
    expect(decideCaptureSync(input({ loaded: false, consentUserId: null })).prompt).toBe(false);
  });
});
