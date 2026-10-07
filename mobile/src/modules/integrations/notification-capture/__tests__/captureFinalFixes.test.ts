// Rodada final de revisão: ordem "escuta antes de ligar o nativo" (M-I6), reenvio que não se sobrepõe (M-M5)
// e leitura do consentimento que falha tratada como "não sei", não como "nunca respondeu" (M-M8).
const secureStore: Record<string, string> = {};

jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn(async (key: string) => secureStore[key] ?? null),
  deleteItemAsync: jest.fn(async (key: string) => {
    delete secureStore[key];
  }),
}));

const mockPost = jest.fn();
jest.mock('@/services/apiClient', () => ({
  __esModule: true,
  default: { post: (...args: unknown[]) => mockPost(...args) },
}));

import * as SecureStore from 'expo-secure-store';
import { getPendingRetryCount, handleRawNotificationEvent } from '../eventUploader';
import { applyCaptureDecision, decideCaptureSync, type CaptureSyncDecision } from '../captureSync';
import { clearUserData } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { EMPTY_CONSENT, acceptCapture, serializeConsent } from '@/modules/privacy/consent';
import { consentStorageKey } from '@/modules/privacy/consentStore';

const PURCHASE = {
  packageName: 'com.nu.production',
  title: 'Compra aprovada',
  body: 'Compra no crédito: R$ 45,90 em PADARIA DO ZE',
  timestampMs: Date.UTC(2026, 9, 5, 14, 30, 0),
};

const getItem = SecureStore.getItemAsync as jest.Mock;

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockPost.mockReset();
  mockPost.mockResolvedValue({ status: 202 });
  getItem.mockImplementation(async (key: string) => secureStore[key] ?? null);
});

describe('applyCaptureDecision: o nativo só é ligado depois que o JS já escuta (M-I6)', () => {
  function effects() {
    const calls: string[] = [];
    return {
      calls,
      attachListener: () => {
        calls.push('attach');
        return () => calls.push('detach');
      },
      setNativeEnabled: (enabled: boolean) => calls.push(`native:${enabled}`),
      clearPending: () => calls.push('clear'),
    };
  }
  const ENABLE: CaptureSyncDecision = { native: 'enable', listen: true, clearPending: false, prompt: false };
  const DISABLE: CaptureSyncDecision = { native: 'disable', listen: false, clearPending: true, prompt: false };
  const LEAVE: CaptureSyncDecision = { native: 'leave', listen: false, clearPending: false, prompt: false };

  it('ligar: primeiro a escuta, depois o nativo (que então entrega o que ficou guardado)', () => {
    const fx = effects();
    applyCaptureDecision(ENABLE, fx);
    expect(fx.calls).toEqual(['attach', 'native:true']);
  });

  it('desligar: avisa o nativo e descarta as pendências, sem escutar', () => {
    const fx = effects();
    const detach = applyCaptureDecision(DISABLE, fx);
    expect(fx.calls).toEqual(['native:false', 'clear']);
    expect(detach).toBeUndefined();
  });

  it('consentimento ainda não conhecido: não toca no nativo', () => {
    const fx = effects();
    applyCaptureDecision(LEAVE, fx);
    expect(fx.calls).toEqual([]);
  });

  it('a limpeza só solta a escuta: NÃO desliga o nativo (a tela principal sai de cena sem ser logout)', () => {
    const fx = effects();
    const detach = applyCaptureDecision(ENABLE, fx);
    detach?.();
    expect(fx.calls).toEqual(['attach', 'native:true', 'detach']);
    expect(fx.calls).not.toContain('native:false');
  });
});

describe('reenvio não se sobrepõe a si mesmo (M-M5)', () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  it('com um reenvio ainda sem resposta, o próximo ciclo não manda o mesmo evento de novo', async () => {
    await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', 'couple-1');
    await useConsentStore.getState().load('user-1');
    await useConsentStore.getState().acceptCapture();

    mockPost.mockReset();
    mockPost.mockRejectedValueOnce(new Error('Network Error')); // envio inicial falha: entra na fila
    await handleRawNotificationEvent(PURCHASE);
    expect(getPendingRetryCount()).toBe(1);

    // O reenvio sai e a resposta demora (o tempo limite da requisição é de 30 s; o ciclo roda a cada 3 s).
    let finish: (() => void) | undefined;
    mockPost.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = () => resolve({ status: 202 });
        }),
    );
    await jest.advanceTimersByTimeAsync(4_000);
    expect(mockPost).toHaveBeenCalledTimes(2);

    // Vários ciclos passam com a requisição em andamento: nenhuma segunda cópia do mesmo evento.
    await jest.advanceTimersByTimeAsync(15_000);
    expect(mockPost).toHaveBeenCalledTimes(2);

    finish?.();
    await jest.advanceTimersByTimeAsync(3_000);
    expect(getPendingRetryCount()).toBe(0);
    await jest.advanceTimersByTimeAsync(30_000);
    expect(mockPost).toHaveBeenCalledTimes(2);
  });
});

describe('leitura do consentimento que falha é "não sei" (M-M8)', () => {
  async function signIn() {
    await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', 'couple-1');
  }

  it('não vira "nunca respondeu": fica sem carregar, marcada como falha', async () => {
    secureStore[consentStorageKey('user-1')] = serializeConsent(acceptCapture(EMPTY_CONSENT, '2026-10-01T12:00:00.000Z'));
    await signIn();
    getItem.mockRejectedValueOnce(new Error('keystore indisponível'));

    await useConsentStore.getState().load('user-1');

    const state = useConsentStore.getState();
    expect(state.loaded).toBe(false);
    expect(state.loadFailed).toBe(true);
    expect(state.userId).toBe('user-1');
  });

  it('com a leitura falhada o nativo é deixado em paz e a tela de consentimento não abre', async () => {
    await signIn();
    getItem.mockRejectedValueOnce(new Error('keystore indisponível'));
    await useConsentStore.getState().load('user-1');

    const { userId, loaded, record } = useConsentStore.getState();
    const decision = decideCaptureSync({
      sessionUserId: 'user-1',
      consentUserId: userId,
      loaded,
      record,
      listenerPermissionGranted: true,
    });

    expect(decision).toEqual({ native: 'leave', listen: false, clearPending: false, prompt: false });
  });

  it('nova tentativa que consegue ler recupera o aceite de quem já tinha aceitado', async () => {
    secureStore[consentStorageKey('user-1')] = serializeConsent(acceptCapture(EMPTY_CONSENT, '2026-10-01T12:00:00.000Z'));
    await signIn();
    getItem.mockRejectedValueOnce(new Error('keystore indisponível'));
    await useConsentStore.getState().load('user-1');
    const attemptsAfterFailure = useConsentStore.getState().loadAttempts;

    await useConsentStore.getState().load('user-1');

    const state = useConsentStore.getState();
    expect(state.loaded).toBe(true);
    expect(state.loadFailed).toBe(false);
    expect(state.record.capture.acceptedAt).toBe('2026-10-01T12:00:00.000Z');
    expect(state.record.capture.enabled).toBe(true);
    expect(state.loadAttempts).toBeGreaterThan(attemptsAfterFailure);
  });

  it('enquanto não se sabe, nada é enviado e nenhuma resposta pode ser gravada por cima', async () => {
    await signIn();
    getItem.mockRejectedValueOnce(new Error('keystore indisponível'));
    await useConsentStore.getState().load('user-1');

    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
    expect(mockPost).not.toHaveBeenCalled();
    expect(await useConsentStore.getState().declineCapture()).toBe(false);
  });

  it('sem registro guardado (leitura deu certo, vazia) continua sendo "nunca respondeu"', async () => {
    await signIn();
    await useConsentStore.getState().load('user-1');

    const state = useConsentStore.getState();
    expect(state.loaded).toBe(true);
    expect(state.loadFailed).toBe(false);
    expect(state.record).toEqual(EMPTY_CONSENT);
  });
});

describe('o que o nativo guardou sem entregar acompanha a fila do JS (M-I6)', () => {
  it('trocar de grupo, retirar o consentimento ou sair descarta também o buffer do nativo', async () => {
    const { clearPendingEvents, registerNativeBufferDropper } = jest.requireActual('../eventUploader') as typeof import('../eventUploader');
    const drop = jest.fn();
    registerNativeBufferDropper(drop);

    clearPendingEvents();
    expect(drop).toHaveBeenCalledTimes(1);

    await clearUserData(); // sair da conta roda os limpadores, entre eles clearPendingEvents
    expect(drop).toHaveBeenCalledTimes(2);
  });

  it('um descartador que falha não impede a limpeza da fila', () => {
    const { clearPendingEvents, registerNativeBufferDropper } = jest.requireActual('../eventUploader') as typeof import('../eventUploader');
    registerNativeBufferDropper(() => {
      throw new Error('módulo nativo indisponível');
    });
    expect(() => clearPendingEvents()).not.toThrow();
    expect(getPendingRetryCount()).toBe(0);
  });
});
