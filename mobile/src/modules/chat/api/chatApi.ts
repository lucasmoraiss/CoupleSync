// AC-AI-Chat: Thin wrapper around the shared chatApiClient
import { chatApiClient } from '@/services/apiClient';
import type { ChatHistoryItem, ChatResponse } from '@/types/api';

export type { ChatHistoryItem, ChatResponse };

export const chatApi = {
  send: (message: string, history: ChatHistoryItem[]): Promise<ChatResponse> =>
    chatApiClient.send(message, history).then((r) => r.data),
};
