// AC-AI-Chat: Thin wrapper around the shared chatApiClient
import { chatApiClient } from '@/services/apiClient';
import { isAiChatAllowedNow } from '@/modules/privacy/consentStore';
import type { ChatHistoryItem, ChatResponse } from '@/types/api';

export type { ChatHistoryItem, ChatResponse };

/** O usuário ainda não aceitou (ou recusou) o envio dos dados financeiros ao Google Gemini. */
export class AiChatConsentError extends Error {
  constructor() {
    super('Aceite o aviso de privacidade do Chat IA para usá-lo.');
    this.name = 'AiChatConsentError';
  }
}

export const chatApi = {
  send: (message: string, history: ChatHistoryItem[]): Promise<ChatResponse> => {
    // Porta de consentimento (SEG-10): sem aceite nada é enviado, nem a mensagem nem o contexto financeiro.
    if (!isAiChatAllowedNow()) return Promise.reject(new AiChatConsentError());
    return chatApiClient.send(message, history).then((r) => r.data);
  },
};
