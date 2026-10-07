// Uma notificação capturada com um grupo ativo nunca é enviada com o token de outro grupo: cada evento na fila de
// reenvio fica preso ao grupo (e à sessão) em que foi capturado e é descartado quando isso muda.
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

import { clearPendingEvents, getPendingRetryCount, handleRawNotificationEvent } from '../eventUploader';
import { useSessionStore } from '@/state/sessionStore';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { activateGroup } from '@/modules/couple/activateGroup';
import { groupSessionDeps } from '@/modules/couple/groupSession';
import { createAppQueryClient } from '@/services/queryClient';

const purchase = {
  packageName: 'com.nu.production',
  title: 'Compra aprovada',
  body: 'Compra no crédito: R$ 45,90 em PADARIA DO ZE',
  timestampMs: Date.UTC(2026, 9, 5, 14, 30, 0),
};

beforeEach(async () => {
  jest.useFakeTimers();
  clearPendingEvents();
  mockPost.mockReset();
  await useSessionStore.getState().setSession('access-A', 'refresh-A', 'user-1', 'group-A');
  await useConsentStore.getState().load('user-1');
  await useConsentStore.getState().acceptCapture();
});

afterEach(() => jest.useRealTimers());

describe('evento na fila de reenvio e troca de grupo', () => {
  it('capturado no grupo A e ainda na fila quando o grupo ativo passa a ser B: é descartado, nunca reenviado', async () => {
    mockPost.mockRejectedValue(new Error('Network Error'));
    await handleRawNotificationEvent(purchase);
    expect(getPendingRetryCount()).toBe(1);
    expect(mockPost).toHaveBeenCalledTimes(1);

    // O grupo muda sem que ninguém esvazie a fila (é o que este teste isola).
    await useSessionStore.getState().setActiveGroup('access-B', 'group-B', 'refresh-B');
    mockPost.mockResolvedValue({ status: 202 });
    await jest.advanceTimersByTimeAsync(60_000);

    expect(mockPost).toHaveBeenCalledTimes(1);
    expect(getPendingRetryCount()).toBe(0);
  });

  it('capturado enquanto a troca de grupo ainda grava a sessão: o envio que falhar não é repetido no grupo novo', async () => {
    const deps = groupSessionDeps(createAppQueryClient());
    mockPost.mockRejectedValue(new Error('Network Error'));

    await activateGroup(
      { accessToken: 'access-B', refreshToken: 'refresh-B', coupleId: 'group-B' },
      {
        ...deps,
        saveSession: async (session) => {
          // A fila já foi esvaziada; a sessão ainda é a do grupo A. Chega uma notificação e o envio falha.
          await handleRawNotificationEvent(purchase);
          await deps.saveSession(session);
        },
      },
    );
    expect(useSessionStore.getState().coupleId).toBe('group-B');
    mockPost.mockResolvedValue({ status: 202 });
    await jest.advanceTimersByTimeAsync(60_000);

    expect(mockPost).toHaveBeenCalledTimes(1); // só a tentativa feita ainda no grupo A
    expect(getPendingRetryCount()).toBe(0);
  });

  it('sem troca de grupo o reenvio continua funcionando (renovar o token não descarta nada)', async () => {
    mockPost.mockRejectedValueOnce(new Error('Network Error'));
    await handleRawNotificationEvent(purchase);
    await useSessionStore.getState().setTokens('access-A2', 'refresh-A2'); // renovação de token, mesmo grupo
    mockPost.mockResolvedValue({ status: 202 });

    await jest.advanceTimersByTimeAsync(4_000);

    expect(mockPost).toHaveBeenCalledTimes(2);
    expect(getPendingRetryCount()).toBe(0);
  });

  it('uma notificação que chega sem grupo ativo não é enviada nem guardada', async () => {
    await useSessionStore.getState().setActiveGroup('access-0', null, 'refresh-0');

    expect(await handleRawNotificationEvent(purchase)).toBe(false);

    expect(mockPost).not.toHaveBeenCalled();
    expect(getPendingRetryCount()).toBe(0);
  });
});
