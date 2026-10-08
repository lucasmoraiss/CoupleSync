// Issue #38, revisão 1 (I6): duas telas abrem sozinhas na chegada à área principal — o consentimento da captura
// (para quem já deu o acesso às notificações no Android) e a pergunta da análise com IA. A ordem é decidida aqui:
// a da captura vem primeiro, e a da IA espera a vez dela. Tudo inventado (ids de exemplo).
import { EMPTY_CONSENT, acceptCapture, declineCapture, markCapturePromptShown, type ConsentRecord } from '@/modules/privacy/consent';
import {
  clearCapturePromptOpening,
  isCapturePromptAhead,
  noteCapturePromptOpening,
  type CapturePromptProbe,
} from '../capturePromptGate';

const NOW = '2026-10-08T15:00:00.000Z';

interface Consent {
  userId: string | null;
  loaded: boolean;
  record: ConsentRecord;
}

function probe(overrides: Partial<CapturePromptProbe> & { consent?: Consent; afterLoad?: Consent } = {}): CapturePromptProbe & { loads: string[]; permissionChecks: number } {
  let consent: Consent = overrides.consent ?? { userId: 'user-1', loaded: true, record: EMPTY_CONSENT };
  const self = {
    loads: [] as string[],
    permissionChecks: 0,
    sessionUserId: 'user-1' as string | null,
    bridgeAvailable: true,
    readConsent: () => consent,
    loadConsent: async (userId: string) => {
      self.loads.push(userId);
      if (overrides.afterLoad) consent = overrides.afterLoad;
    },
    checkPermission: async () => {
      self.permissionChecks += 1;
      return true;
    },
    ...overrides,
  };
  return self;
}

beforeEach(() => {
  clearCapturePromptOpening();
});

describe('o consentimento da captura vai abrir sozinho?', () => {
  it('acesso às notificações já dado e nenhuma resposta registrada: sim, a tela da captura vem primeiro', async () => {
    await expect(isCapturePromptAhead(probe())).resolves.toBe(true);
  });

  it('sem o acesso às notificações do Android (a maioria das pessoas): não, a pergunta da IA pode abrir', async () => {
    await expect(isCapturePromptAhead(probe({ checkPermission: async () => false }))).resolves.toBe(false);
  });

  it('a captura já foi respondida (aceita ou recusada): não, e nem consulta o Android', async () => {
    for (const record of [acceptCapture(EMPTY_CONSENT, NOW), declineCapture(EMPTY_CONSENT, NOW)]) {
      const p = probe({ consent: { userId: 'user-1', loaded: true, record } });
      await expect(isCapturePromptAhead(p)).resolves.toBe(false);
      expect(p.permissionChecks).toBe(0);
    }
  });

  it('a tela da captura já abriu numa outra abertura do app e a pessoa não respondeu: não abre de novo, a IA pode perguntar', async () => {
    const record = markCapturePromptShown(EMPTY_CONSENT, NOW);
    await expect(isCapturePromptAhead(probe({ consent: { userId: 'user-1', loaded: true, record } }))).resolves.toBe(false);
  });

  it('a tela da captura está abrindo AGORA (já marcada como mostrada, ainda sem resposta): sim — as duas não disputam', async () => {
    noteCapturePromptOpening('user-1');
    const record = markCapturePromptShown(EMPTY_CONSENT, NOW);

    await expect(isCapturePromptAhead(probe({ consent: { userId: 'user-1', loaded: true, record } }))).resolves.toBe(true);

    // Respondida, deixa de segurar a pergunta da IA.
    await expect(isCapturePromptAhead(probe({ consent: { userId: 'user-1', loaded: true, record: acceptCapture(record, NOW) } }))).resolves.toBe(false);
  });

  it('a marca de "abrindo agora" é de uma pessoa: não segura a pergunta da IA de outra', async () => {
    noteCapturePromptOpening('user-2');
    const record = markCapturePromptShown(EMPTY_CONSENT, NOW);
    await expect(isCapturePromptAhead(probe({ consent: { userId: 'user-1', loaded: true, record } }))).resolves.toBe(false);
  });

  it('registro ainda não lido (ex.: logo depois de criar o grupo): lê antes de decidir', async () => {
    const p = probe({
      consent: { userId: null, loaded: false, record: EMPTY_CONSENT },
      afterLoad: { userId: 'user-1', loaded: true, record: EMPTY_CONSENT },
    });

    await expect(isCapturePromptAhead(p)).resolves.toBe(true);
    expect(p.loads).toEqual(['user-1']);
  });

  it('registro que não pôde ser lido, registro de outra pessoa, sem sessão, sem o leitor nativo: não segura a pergunta da IA', async () => {
    const unread = { userId: 'user-1', loaded: false, record: EMPTY_CONSENT };
    await expect(isCapturePromptAhead(probe({ consent: unread, afterLoad: unread }))).resolves.toBe(false);
    const other = { userId: 'user-2', loaded: true, record: EMPTY_CONSENT };
    await expect(isCapturePromptAhead(probe({ consent: other, afterLoad: other }))).resolves.toBe(false);
    await expect(isCapturePromptAhead(probe({ sessionUserId: null }))).resolves.toBe(false);
    await expect(isCapturePromptAhead(probe({ bridgeAvailable: false }))).resolves.toBe(false);
  });

  it('a consulta da permissão falhou: não segura a pergunta da IA', async () => {
    const checkPermission = async (): Promise<boolean> => {
      throw new Error('ponte nativa');
    };
    await expect(isCapturePromptAhead(probe({ checkPermission }))).resolves.toBe(false);
  });
});
