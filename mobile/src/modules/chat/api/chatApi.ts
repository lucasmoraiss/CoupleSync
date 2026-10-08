// AC-AI-Chat: Thin wrapper around the shared chatApiClient.
// Quem decide se o Assistente pode responder é o servidor (GET /ai/status: `available` e `enabled`; o envio é
// recusado com 403 AI_CONSENT_REQUIRED sem a ativação do grupo). O aceite antigo guardado no aparelho não é consultado.
import { chatApiClient } from '@/services/apiClient';
import type { ChatHistoryItem, ChatResponse } from '@/types/api';

export type { ChatHistoryItem, ChatResponse };

export const chatApi = {
  send: (message: string, history: ChatHistoryItem[]): Promise<ChatResponse> =>
    chatApiClient.send(message, history).then((r) => r.data),
};
