// SEG-10: sem aceite do aviso, o chat não envia nada ao servidor (que repassa ao Google Gemini).
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

const mockSend = jest.fn();
jest.mock('@/services/apiClient', () => ({
  chatApiClient: { send: (...args: unknown[]) => mockSend(...args) },
}));

import { AiChatConsentError, chatApi } from '../api/chatApi';
import { clearUserData } from '@/state/userData';
import { useSessionStore } from '@/state/sessionStore';
import { useConsentStore } from '@/modules/privacy/consentStore';

async function signIn(userId: string) {
  await useSessionStore.getState().setSession(`access-${userId}`, `refresh-${userId}`, userId, 'couple-1');
  await useConsentStore.getState().load(userId);
}

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockSend.mockReset();
  mockSend.mockResolvedValue({ data: { reply: 'oi' } });
});

describe('chatApi.send e o aviso de privacidade', () => {
  it('sem aceite rejeita e não chama o servidor', async () => {
    await signIn('user-1');
    await expect(chatApi.send('Quanto gastei?', [])).rejects.toBeInstanceOf(AiChatConsentError);
    expect(mockSend).not.toHaveBeenCalled();
  });

  it('depois de "não usar" continua sem enviar', async () => {
    await signIn('user-1');
    await useConsentStore.getState().declineAiChat();
    await expect(chatApi.send('Quanto gastei?', [])).rejects.toBeInstanceOf(AiChatConsentError);
    expect(mockSend).not.toHaveBeenCalled();
  });

  it('depois de aceitar envia', async () => {
    await signIn('user-1');
    await useConsentStore.getState().acceptAiChat();
    await expect(chatApi.send('Quanto gastei?', [])).resolves.toEqual({ reply: 'oi' });
    expect(mockSend).toHaveBeenCalledTimes(1);
  });

  it('o aceite de um usuário não vale para o próximo no mesmo aparelho', async () => {
    await signIn('user-1');
    await useConsentStore.getState().acceptAiChat();
    await clearUserData();

    await signIn('user-2');

    await expect(chatApi.send('Quanto gastei?', [])).rejects.toBeInstanceOf(AiChatConsentError);
    expect(mockSend).not.toHaveBeenCalled();
  });
});
