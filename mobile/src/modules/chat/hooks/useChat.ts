// AC-AI-Chat: Ephemeral chat state hook — history lives only for the session
import { useState, useCallback } from 'react';
import { useMutation } from '@tanstack/react-query';
import { chatApi } from '../api/chatApi';
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
    onError: (err: any) => {
      const status = err?.response?.status;
      if (status === 404) {
        setError('AI Chat não disponível');
      } else if (status === 429) {
        setError('Limite atingido, tente novamente em breve');
      } else {
        setError('Erro ao enviar mensagem. Tente novamente.');
      }
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

      // Snapshot history before the new user message (what backend needs as context)
      // Trim to last 20 messages to cap request size
      const history: ChatHistoryItem[] = messages.slice(-20).map((m) => ({
        role: m.role,
        content: m.content,
      }));

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
