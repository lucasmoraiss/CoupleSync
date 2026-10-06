import { describeProgress, describeSlices, spokenBRL } from '../a11y';

describe('spokenBRL', () => {
  it.each([
    [0, 'zero reais'],
    [1, '1 real'],
    [2, '2 reais'],
    [42.9, '42 reais e 90 centavos'],
    [1234.5, '1234 reais e 50 centavos'],
    [0.01, '1 centavo'],
    [0.5, '50 centavos'],
    [1.01, '1 real e 1 centavo'],
    [-10, 'menos 10 reais'],
    [-0.5, 'menos 50 centavos'],
    [999999999.99, '999999999 reais e 99 centavos'],
  ])('%p -> %s', (value, expected) => {
    expect(spokenBRL(value)).toBe(expected);
  });

  it('não sofre com erro de ponto flutuante', () => {
    expect(spokenBRL(0.1 + 0.2)).toBe('30 centavos');
    expect(spokenBRL(19.99)).toBe('19 reais e 99 centavos');
  });

  it('valor ausente ou inválido não gera texto', () => {
    expect(spokenBRL(null)).toBe('');
    expect(spokenBRL(undefined)).toBe('');
    expect(spokenBRL(NaN)).toBe('');
  });

  it('o rótulo nunca contém o símbolo R$ nem separador de milhar', () => {
    expect(spokenBRL(1234567.89)).not.toMatch(/R\$|\./);
  });
});

describe('describeSlices', () => {
  it('resume o gráfico com total e percentuais', () => {
    expect(
      describeSlices('Gastos por categoria', [
        { label: 'Alimentação', value: 450 },
        { label: 'Transporte', value: 550 },
      ]),
    ).toBe('Gastos por categoria, total 1000 reais. Alimentação: 450 reais (45%). Transporte: 550 reais (55%).');
  });

  it('diz quando não há dados e quando itens ficam de fora', () => {
    expect(describeSlices('Gastos por categoria', [])).toBe('Gastos por categoria: sem dados.');
    const many = Array.from({ length: 10 }, (_, i) => ({ label: `C${i}`, value: 10 }));
    expect(describeSlices('X', many, { limit: 3 })).toMatch(/Mais 7 itens\.$/);
  });

  it('sem percentual para séries que não somam um todo (gastos por mês)', () => {
    expect(describeSlices('Gastos mensais', [{ label: 'jan', value: 100 }], { showPercent: false })).toBe(
      'Gastos mensais. jan: 100 reais.',
    );
  });
});

describe('describeProgress', () => {
  it('limita o percentual a 0–100 e inclui os valores', () => {
    expect(describeProgress('Meta casa', 40, 400, 1000)).toBe('Meta casa: 40% concluída, 400 reais de 1000 reais.');
    expect(describeProgress('Meta casa', 140)).toBe('Meta casa: 100% concluída.');
  });
});
