// Eventos capturados e ainda não enviados pertencem ao usuário que sai: sumir ao sair da conta.
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

describe('fila de reenvio de notificações', () => {
  it('é esvaziada ao sair da conta', async () => {
    mockPost.mockRejectedValue(new Error('Network Error'));
    const queued = await handleRawNotificationEvent({
      packageName: 'com.nu.production',
      title: 'Compra aprovada',
      body: 'Compra no crédito: R$ 45,90 em PADARIA DO ZE',
      timestampMs: Date.UTC(2026, 9, 5, 14, 30, 0),
    });
    expect(queued).toBe(true);
    expect(getPendingRetryCount()).toBe(1);

    await clearUserData();

    expect(getPendingRetryCount()).toBe(0);
  });
});
