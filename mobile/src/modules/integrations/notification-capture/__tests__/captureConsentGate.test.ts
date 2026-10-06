// Consentimento da captura: sem aceite, ou com o interruptor desligado, nada é lido nem enviado.
// O consentimento é por usuário: outro usuário no mesmo aparelho é perguntado de novo.
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

import { getPendingRetryCount, handleRawNotificationEvent } from '../eventUploader';
import { clearUserData } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';
import { consentStorageKey, useConsentStore } from '@/modules/privacy/consentStore';

const PURCHASE = {
  packageName: 'com.nu.production',
  title: 'Compra aprovada',
  body: 'Compra no crédito: R$ 45,90 em PADARIA DO ZE',
  timestampMs: Date.UTC(2026, 9, 5, 14, 30, 0),
};

async function signIn(userId: string) {
  await useSessionStore.getState().setSession(`access-${userId}`, `refresh-${userId}`, userId, 'couple-1');
  await useConsentStore.getState().load(userId);
}

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockPost.mockReset();
  mockPost.mockResolvedValue({ status: 202 });
});

describe('porta de consentimento da captura', () => {
  it('sem aceite nada é enviado', async () => {
    await signIn('user-1');

    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
    expect(mockPost).not.toHaveBeenCalled();
  });

  it('antes de o registro carregar nada é enviado', async () => {
    await useSessionStore.getState().setSession('a', 'r', 'user-1', 'couple-1');

    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
    expect(mockPost).not.toHaveBeenCalled();
  });

  it('depois do aceite envia; desligar o interruptor para de enviar na hora', async () => {
    await signIn('user-1');
    await useConsentStore.getState().acceptCapture();

    expect(await handleRawNotificationEvent(PURCHASE)).toBe(true);
    expect(mockPost).toHaveBeenCalledTimes(1);

    await useConsentStore.getState().setCaptureEnabled(false);
    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
    expect(mockPost).toHaveBeenCalledTimes(1);
  });

  it('o interruptor sem aceite anterior não liga a captura', async () => {
    await signIn('user-1');
    await useConsentStore.getState().setCaptureEnabled(true);

    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
    expect(mockPost).not.toHaveBeenCalled();
  });

  it('o aceite sobrevive a sair e entrar com o mesmo usuário', async () => {
    await signIn('user-1');
    await useConsentStore.getState().acceptCapture();
    const acceptedAt = useConsentStore.getState().record.capture.acceptedAt;

    await clearUserData(); // sair
    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false); // sem sessão, nada

    await signIn('user-1');
    expect(useConsentStore.getState().record.capture.acceptedAt).toBe(acceptedAt);
    expect(await handleRawNotificationEvent(PURCHASE)).toBe(true);
  });

  it('outro usuário no mesmo aparelho é perguntado de novo e não herda o aceite', async () => {
    await signIn('user-1');
    await useConsentStore.getState().acceptCapture();
    await clearUserData();

    await signIn('user-2');

    expect(useConsentStore.getState().record.capture.acceptedAt).toBeNull();
    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
    expect(mockPost).not.toHaveBeenCalled();
    expect(secureStore[consentStorageKey('user-1')]).toBeDefined();
    expect(secureStore[consentStorageKey('user-2')]).toBeUndefined();
  });

  it('um registro que carrega depois de a sessão mudar não vale para o novo usuário', async () => {
    await useSessionStore.getState().setSession('a1', 'r1', 'user-1', 'couple-1');
    const loading = useConsentStore.getState().load('user-1');
    await clearUserData();
    await useSessionStore.getState().setSession('a2', 'r2', 'user-2', 'couple-1');
    await loading;

    expect(useConsentStore.getState().userId).not.toBe('user-1');
    expect(await handleRawNotificationEvent(PURCHASE)).toBe(false);
  });

  it('eventos pendentes não saem depois que o consentimento é retirado', async () => {
    jest.useFakeTimers();
    try {
      await signIn('user-1');
      await useConsentStore.getState().acceptCapture();
      mockPost.mockRejectedValueOnce(new Error('Network Error'));
      await handleRawNotificationEvent(PURCHASE);
      expect(getPendingRetryCount()).toBe(1);

      await useConsentStore.getState().setCaptureEnabled(false);
      await jest.advanceTimersByTimeAsync(10_000);

      expect(getPendingRetryCount()).toBe(0);
      expect(mockPost).toHaveBeenCalledTimes(1); // só a tentativa inicial
    } finally {
      jest.useRealTimers();
    }
  });
});
