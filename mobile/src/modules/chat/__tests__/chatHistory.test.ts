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
    const long = `Faltam R$ 10,00 para "Carro". ${'Os gastos subiram. '.repeat(130)}`;

    const [item] = buildChatHistory([model(long)]);

    expect(item.content.startsWith('Faltam R$ 10,00 para "Carro". Os gastos subiram.')).toBe(true);
    expect(item.content.length).toBeGreaterThan(1900);
    expect(item.content.length).toBeLessThanOrEqual(2000);
  });

  it('o corte não parte um emoji ao meio (texto inválido seria recusado pelo servidor)', () => {
    const before = 'a '.repeat(999); // 1.998 caracteres; o emoji ocupa as posições 1.999 e 2.000
    const [item] = buildChatHistory([model(`${before}b\u{1F600} e mais ${'b '.repeat(100)}`)]);

    expect(item.content).toBe(before.trimEnd());
  });

  // Re-revisão 2, defeito novo 1: o corte acontece antes de o servidor filtrar; um nome partido ("Mariana" →
  // "Marian") não seria reconhecido lá e iria ao provedor. O corte é no último espaço antes do limite.
  it('o corte é no último espaço antes do limite: um nome que atravessa a posição 2.000 não vai pela metade', () => {
    const before = 'Gasto alto. '.repeat(166); // 1.992 caracteres
    const long = `${before}a Mariana gastou mais do que no mês passado, e a conta de luz também subiu.`;
    expect(long.slice(0, 2000).endsWith('a Marian')).toBe(true);

    const [item] = buildChatHistory([model(long)]);

    expect(item.content).toBe(`${before}a`);
    expect(item.content).not.toContain('Mari');
  });

  it('a palavra que termina exatamente no limite fica inteira', () => {
    const before = 'Gasto alto. '.repeat(166); // 1.992 caracteres
    const exact = `${before}Mariana.`; // 2.000 caracteres, e o seguinte é um espaço

    expect(exact).toHaveLength(2000);
    expect(buildChatHistory([model(`${exact} E mais texto depois do limite.`)])[0].content).toBe(exact);
    // "Mariana" ocupa 1.992 a 1.998 e a palavra seguinte atravessa o limite: o nome fica, a palavra não.
    expect(buildChatHistory([model(`${before}Mariana ainda gastou mais.`)])[0].content).toBe(`${before}Mariana`);
  });

  it('texto longo sem nenhum espaço antes do limite não vai: não há onde cortar sem partir uma palavra', () => {
    expect(buildChatHistory([model('a'.repeat(2400)), user('E agora?')])).toEqual([{ role: 'user', content: 'E agora?' }]);
    expect(buildChatHistory([model(`${'a'.repeat(1999)}\u{1F600} e mais`)])).toEqual([]);
  });

  // Re-revisão 2, defeito novo 3: contar aspas fazia uma aspa solta (polegadas) jogar fora quase toda a resposta.
  it('uma aspa solta no começo de uma resposta longa não faz o histórico perder a resposta', () => {
    const long = `Uma tela de 5" custa menos. ${'Os gastos subiram. '.repeat(130)}`;

    const [item] = buildChatHistory([model(long)]);

    expect(item.content.startsWith('Uma tela de 5" custa menos. Os gastos subiram.')).toBe(true);
    expect(item.content.length).toBeGreaterThan(1900);
  });

  it('com uma aspa solta antes, o nome de meta que atravessa o limite continua não indo pela metade', () => {
    const start = 'Uma tela de 5" custa menos. ';
    const filler = 'a'.repeat(1985 - start.length);
    const long = `${start}${filler} para "Viagem para Recife" e mais texto depois.`;

    const [item] = buildChatHistory([model(long)]);

    expect(item.content).toBe(`${start}${filler} para`);
  });

  it('o nome de meta fechado logo antes do corte fica, mesmo com outro trecho entre aspas depois', () => {
    const before = 'Gasto alto. '.repeat(164); // 1.968 caracteres
    const long = `${before}Faltam R$ 1,00 para "Carro" e "Viagem para Recife" neste mês.`;
    expect(long.slice(0, 2000).endsWith('"Carro" e "V')).toBe(true);

    const [item] = buildChatHistory([model(long)]);

    expect(item.content).toBe(`${before}Faltam R$ 1,00 para "Carro" e`);
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
