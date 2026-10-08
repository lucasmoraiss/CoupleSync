// AC-AI-Chat: Ephemeral chat state hook — history lives only for the session
import { useState, useCallback } from 'react';
import { useMutation } from '@tanstack/react-query';
import { chatErrorChangesStatus, chatErrorMessage } from '@/modules/ai/aiStatus';
import { useAiStatusStore } from '@/modules/ai/aiStatusStore';
import { chatApi } from '../api/chatApi';
import { buildChatHistory } from '../chatHistory';
import type { ChatHistoryItem } from '../api/chatApi';

export interface Message {
  id: string;
  role: 'user' | 'model';
  content: string;
}

export interface UseChatReturn {
  messages: Message[];
  isLoading: boolean;
  error: string | null;
  sendMessage: (text: string) => void;
  clearError: () => void;
}

export function useChat(): UseChatReturn {
  const [messages, setMessages] = useState<Message[]>([]);
  const [error, setError] = useState<string | null>(null);

  const mutation = useMutation({
    mutationFn: ({
      message,
      history,
    }: {
      message: string;
      history: ChatHistoryItem[];
    }) => chatApi.send(message, history),
    onSuccess: (data) => {
      setMessages((prev) => [
        ...prev,
        { id: `model-${Date.now()}`, role: 'model', content: data.reply },
      ]);
      setError(null);
    },
    onError: (err: unknown) => {
      // Cota do grupo, limite do app, IA fora do ar, sem internet: cada desfecho tem a sua frase.
      setError(chatErrorMessage(err));
      // A IA foi desligada (no servidor ou pelo outro membro) no meio da conversa: a tela passa a mostrar isso.
      if (chatErrorChangesStatus(err)) void useAiStatusStore.getState().refresh();
    },
  });

  const sendMessage = useCallback(
    (text: string) => {
      const trimmed = text.trim();
      if (!trimmed || mutation.isPending) return;

      setError(null);

      const userMessage: Message = {
        id: `user-${Date.now()}`,
        role: 'user',
        content: trimmed,
      };

      // Snapshot history before the new user message (what backend needs as context): the last 20 messages,
      // each within what the server accepts — a long answer sent back whole would make every next question fail.
      const history: ChatHistoryItem[] = buildChatHistory(messages);

      setMessages((prev) => [...prev, userMessage]);
      mutation.mutate({ message: trimmed, history });
    },
    [messages, mutation]
  );

  return {
    messages,
    isLoading: mutation.isPending,
    error,
    sendMessage,
    clearError: () => setError(null),
  };
}
