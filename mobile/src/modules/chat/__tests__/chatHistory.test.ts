// Issue #38, revisão 2 (I2): o app manda as respostas anteriores de volta como histórico. Uma resposta mais longa
// do que o servidor aceita num item (2.000 caracteres na API que estava publicada) fazia TODA pergunta seguinte da
// conversa voltar 400. O histórico é cortado ao montar o pedido. Textos inventados.
import { buildChatHistory, HISTORY_ITEM_MAX_LENGTH, HISTORY_MAX_ITEMS } from '../chatHistory';

const user = (content: string) => ({ id: `u-${content.length}`, role: 'user' as const, content });
const model = (content: string) => ({ id: `m-${content.length}`, role: 'model' as const, content });

describe('histórico enviado com a pergunta', () => {
  it('mensagens curtas vão inteiras, só com o autor e o texto', () => {
    expect(buildChatHistory([user('Quanto gastamos?'), model('R$ 100,00 em mercado.')])).toEqual([
      { role: 'user', content: 'Quanto gastamos?' },
      { role: 'model', content: 'R$ 100,00 em mercado.' },
    ]);
  });

  it('uma resposta longa (2.400 caracteres) vai cortada no limite do servidor: a pergunta seguinte não é recusada', () => {
    const long = 'Os gastos subiram. '.repeat(200).slice(0, 2400);

    const [item] = buildChatHistory([model(long)]);

    expect(HISTORY_ITEM_MAX_LENGTH).toBe(2000);
    expect(item.content.length).toBeLessThanOrEqual(2000);
    expect(item.content.length).toBeGreaterThan(1900);
    expect(long.startsWith(item.content)).toBe(true);
  });

  it('o corte não deixa metade de um trecho entre aspas (o nome de uma meta, que o servidor só reconhece inteiro)', () => {
    const long = `${'a'.repeat(1985)} para "Viagem para Recife" e mais texto depois.`;

    const [item] = buildChatHistory([model(long)]);

    expect(item.content).toBe(`${'a'.repeat(1985)} para`);
  });

  it('aspas que fecham antes do corte ficam', () => {
    const long = `Faltam R$ 10,00 para "Carro". ${'a'.repeat(2400)}`;

    const [item] = buildChatHistory([model(long)]);

    expect(item.content.startsWith('Faltam R$ 10,00 para "Carro". aaa')).toBe(true);
    expect(item.content.length).toBe(2000);
  });

  it('o corte não parte um emoji ao meio (texto inválido seria recusado pelo servidor)', () => {
    const [item] = buildChatHistory([model(`${'a'.repeat(1999)}\u{1F600} e mais ${'b'.repeat(100)}`)]);

    expect(item.content).toBe('a'.repeat(1999));
  });

  it('só as 20 últimas mensagens vão; o que sobra vazio depois do corte não vai', () => {
    const many = Array.from({ length: 25 }, (_, i) => user(`pergunta ${i + 1}`));

    const history = buildChatHistory(many);

    expect(HISTORY_MAX_ITEMS).toBe(20);
    expect(history).toHaveLength(20);
    expect(history[0].content).toBe('pergunta 6');
    expect(buildChatHistory([model(`"${'a'.repeat(2400)}"`), user('E agora?')])).toEqual([{ role: 'user', content: 'E agora?' }]);
  });
});
