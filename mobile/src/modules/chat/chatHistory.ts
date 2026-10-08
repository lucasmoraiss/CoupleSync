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

/** O maior nome de meta que o servidor aceita: um trecho entre aspas mais longo do que isso não é nome de meta. */
const GOAL_TITLE_MAX_LENGTH = 128;

const WHITE_SPACE = /\s/;

/** O fim do texto que cabe no limite sem partir palavra: o último espaço em branco até lá; -1 quando não há. */
function lastWordEnd(text: string): number {
  for (let i = HISTORY_ITEM_MAX_LENGTH; i >= 0; i--) {
    if (WHITE_SPACE.test(text.charAt(i))) return i;
  }
  return -1;
}

/**
 * Onde começa o trecho entre aspas que o fim `end` partiria, ou -1. É entre aspas que a resposta mostra o nome de
 * uma meta ("Viagem para Recife"), e o servidor só o reconhece inteiro. O trecho é o que vai da última aspa antes
 * de `end` até a primeira depois, quando tem o jeito de um nome reposto: cabe no tamanho de um nome de meta e não
 * começa nem termina com espaço (isso separa o nome do texto que fica ENTRE dois nomes). Não se contam as aspas do
 * texto: uma aspa solta lá no começo (5") não muda o que acontece aqui.
 */
function openQuoteBefore(text: string, end: number): number {
  const open = text.lastIndexOf('"', end - 1);
  const close = text.indexOf('"', end);
  if (open < 0 || close < 0) return -1;
  const inside = text.slice(open + 1, close);
  if (inside.length === 0 || inside.length > GOAL_TITLE_MAX_LENGTH) return -1;
  if (WHITE_SPACE.test(inside.charAt(0)) || WHITE_SPACE.test(inside.charAt(inside.length - 1))) return -1;
  return open;
}

function cut(text: string): string {
  if (text.length <= HISTORY_ITEM_MAX_LENGTH) return text;
  // O corte acontece ANTES do filtro de privacidade do servidor, que só reconhece palavra inteira: meio nome de
  // uma pessoa do grupo ("Mari", de "Mariana") passaria por ele. Por isso o corte é sempre num espaço em branco
  // — o que também nunca parte um caractere de dois códigos (emoji). Sem espaço nenhum, não há corte seguro e
  // o item não vai.
  let end = lastWordEnd(text);
  if (end < 0) return '';
  for (let open = openQuoteBefore(text, end); open >= 0; open = openQuoteBefore(text, end)) end = open;
  return text.slice(0, end).trimEnd();
}

/** As últimas mensagens da conversa, no formato do pedido, cada uma dentro do limite; o que fica vazio não vai. */
export function buildChatHistory(messages: readonly HistorySource[]): ChatHistoryItem[] {
  return messages
    .slice(-HISTORY_MAX_ITEMS)
    .map((m) => ({ role: m.role, content: cut(m.content) }))
    .filter((m) => m.content.length > 0);
}
