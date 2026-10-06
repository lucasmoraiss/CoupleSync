import { getTransactionSubtitle, getTransactionTitle } from '../display';

const base = { merchant: null as string | null, description: null as string | null, bank: 'NUBANK' };

describe('getTransactionTitle', () => {
  it('prefere o estabelecimento, depois a descrição, depois o banco', () => {
    expect(getTransactionTitle({ ...base, merchant: 'Loja', description: 'Compra' })).toBe('Loja');
    expect(getTransactionTitle({ ...base, description: 'Compra' })).toBe('Compra');
    expect(getTransactionTitle(base)).toBe('NUBANK');
  });

  it('texto em branco conta como ausente', () => {
    expect(getTransactionTitle({ ...base, merchant: '  ', description: 'Compra' })).toBe('Compra');
  });
});

describe('getTransactionSubtitle', () => {
  it('mostra a descrição quando há estabelecimento e ela é diferente', () => {
    expect(getTransactionSubtitle({ ...base, merchant: 'Loja', description: 'Compra' })).toBe('Compra');
  });

  it('não repete quando a descrição é igual ao estabelecimento (sem diferenciar caixa)', () => {
    expect(getTransactionSubtitle({ ...base, merchant: 'Loja', description: ' loja ' })).toBeNull();
  });

  it('sem estabelecimento a descrição já é o título, então não há subtítulo', () => {
    expect(getTransactionSubtitle({ ...base, description: 'Compra' })).toBeNull();
  });

  it('sem descrição não há subtítulo', () => {
    expect(getTransactionSubtitle({ ...base, merchant: 'Loja' })).toBeNull();
  });
});
