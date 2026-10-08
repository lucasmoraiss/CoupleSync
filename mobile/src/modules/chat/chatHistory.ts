// O histórico que vai com cada pergunta ao Assistente (lógica pura, coberta por __tests__/chatHistory.test.ts).
//
// O app manda as últimas mensagens da conversa, e as respostas do Assistente entre elas. Uma resposta pode ser
// mais longa do que o servidor aceita num item do histórico; mandada inteira, a pergunta seguinte — e todas as
// outras da conversa — era recusada. Por isso cada item é cortado aqui. O servidor corta de novo, depois do
// filtro de privacidade; o corte daqui vale também para uma API mais antiga, que recusa em vez de cortar.
import type { ChatHistoryItem } from '@/types/api';

/** Quantas mensagens o servidor aceita no histórico. */
export const HISTORY_MAX_ITEMS = 20;

/** O tamanho de um item que toda versão da API aceita (e o que o servidor aproveita de cada mensagem). */
export const HISTORY_ITEM_MAX_LENGTH = 2000;

export interface HistorySource {
  readonly role: 'user' | 'model';
  readonly content: string;
}

function cut(text: string): string {
  if (text.length <= HISTORY_ITEM_MAX_LENGTH) return text;
  let kept = text.slice(0, HISTORY_ITEM_MAX_LENGTH);
  // Metade de um caractere de dois códigos (emoji) não é texto válido: o servidor recusaria o pedido.
  const last = kept.charCodeAt(kept.length - 1);
  if (last >= 0xd800 && last <= 0xdbff) kept = kept.slice(0, -1);
  // Aspas abertas e não fechadas: o corte caiu dentro de um trecho entre aspas. É entre aspas que a resposta
  // mostra o nome de uma meta, e o servidor só o reconhece inteiro — o pedaço não vai.
  const quotes = kept.split('"').length - 1;
  if (quotes % 2 === 1) kept = kept.slice(0, kept.lastIndexOf('"'));
  return kept.trimEnd();
}

/** As últimas mensagens da conversa, no formato do pedido, cada uma dentro do limite; o que fica vazio não vai. */
export function buildChatHistory(messages: readonly HistorySource[]): ChatHistoryItem[] {
  return messages
    .slice(-HISTORY_MAX_ITEMS)
    .map((m) => ({ role: m.role, content: cut(m.content) }))
    .filter((m) => m.content.length > 0);
}
