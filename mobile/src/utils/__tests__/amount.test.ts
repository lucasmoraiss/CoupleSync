import {
  AMOUNT_MESSAGES,
  MAX_AMOUNT,
  MAX_AMOUNT_CENTS,
  amountCentsError,
  centsFromDigits,
  parseAmountText,
  validateAmount,
  validateAmountCents,
} from '../amount';

describe('parseAmountText', () => {
  it.each([
    ['42,90', 42.9],
    ['42.90', 42.9],
    ['42', 42],
    ['0,01', 0.01],
    ['1.234,56', 1234.56],
    ['R$ 1.234,56', 1234.56],
    ['  10,5 ', 10.5],
    ['12.5', 12.5],
    ['999.999.999,99', 999_999_999.99],
    ['999999999.99', 999_999_999.99],
  ])('aceita "%s"', (text, value) => {
    expect(parseAmountText(text)).toEqual({ ok: true, value });
  });

  it.each(['', '   ', 'abc', '12abc', '1,2,3', ',', '.', 'R$'])('rejeita o texto inválido "%s"', (text) => {
    expect(parseAmountText(text)).toEqual({ ok: false, message: AMOUNT_MESSAGES.invalid });
  });

  it.each(['0', '0,00', '0.00'])('rejeita zero ("%s")', (text) => {
    expect(parseAmountText(text)).toEqual({ ok: false, message: AMOUNT_MESSAGES.notPositive });
  });

  it('rejeita negativo', () => {
    expect(parseAmountText('-5')).toEqual({ ok: false, message: AMOUNT_MESSAGES.negative });
  });

  it.each(['10,001', '10.001', '10,505', '0,001', '1.234,567'])('rejeita três casas decimais ("%s") sem arredondar', (text) => {
    expect(parseAmountText(text)).toEqual({ ok: false, message: AMOUNT_MESSAGES.tooManyDecimals });
  });

  it.each(['1.000.000.000,00', '1000000000', '999999999,995', '999.999.999,991'])('rejeita acima do teto ("%s")', (text) => {
    const result = parseAmountText(text);
    expect(result.ok).toBe(false);
  });

  it('o teto exato passa e um centavo a mais não', () => {
    expect(parseAmountText('999999999,99').ok).toBe(true);
    expect(parseAmountText('1000000000,00')).toEqual({ ok: false, message: AMOUNT_MESSAGES.tooLarge });
  });

  it('permite zero quando a tela aceita (valor acumulado de meta)', () => {
    expect(parseAmountText('0', { allowZero: true })).toEqual({ ok: true, value: 0 });
  });

  it('mensagens em português', () => {
    expect(AMOUNT_MESSAGES.tooLarge).toBe('O valor máximo é R$ 999.999.999,99.');
    expect(AMOUNT_MESSAGES.tooManyDecimals).toContain('duas casas decimais');
    expect(AMOUNT_MESSAGES.notPositive).toContain('maior que zero');
  });
});

describe('validateAmount', () => {
  it('aplica as mesmas regras a um número', () => {
    expect(validateAmount(0.01).ok).toBe(true);
    expect(validateAmount(MAX_AMOUNT).ok).toBe(true);
    expect(validateAmount(0).ok).toBe(false);
    expect(validateAmount(-1).ok).toBe(false);
    expect(validateAmount(MAX_AMOUNT + 0.01).ok).toBe(false);
    expect(validateAmount(10.005).ok).toBe(false);
    expect(validateAmount(Number.NaN).ok).toBe(false);
    expect(validateAmount(Number.POSITIVE_INFINITY).ok).toBe(false);
  });
});

describe('validateAmountCents', () => {
  it('aceita de 1 centavo ao teto', () => {
    expect(validateAmountCents(1)).toEqual({ ok: true, value: 0.01 });
    expect(validateAmountCents(MAX_AMOUNT_CENTS)).toEqual({ ok: true, value: MAX_AMOUNT });
  });

  it('rejeita zero, negativo, acima do teto e não inteiro', () => {
    expect(validateAmountCents(0)).toEqual({ ok: false, message: AMOUNT_MESSAGES.notPositive });
    expect(validateAmountCents(-1)).toEqual({ ok: false, message: AMOUNT_MESSAGES.negative });
    expect(validateAmountCents(MAX_AMOUNT_CENTS + 1)).toEqual({ ok: false, message: AMOUNT_MESSAGES.tooLarge });
    expect(validateAmountCents(10.5).ok).toBe(false);
  });

  it('zero só passa quando permitido', () => {
    expect(validateAmountCents(0, { allowZero: true })).toEqual({ ok: true, value: 0 });
  });

  it('amountCentsError devolve a mensagem ou null', () => {
    expect(amountCentsError(0)).toBe(AMOUNT_MESSAGES.notPositive);
    expect(amountCentsError(1234)).toBeNull();
  });
});

describe('centsFromDigits', () => {
  it('lê só os dígitos', () => {
    expect(centsFromDigits('1.234,56')).toBe(123456);
    expect(centsFromDigits('')).toBe(0);
    expect(centsFromDigits('abc')).toBe(0);
  });

  it('nunca passa do teto, mesmo com dígitos de sobra', () => {
    expect(centsFromDigits('99999999999')).toBe(99_999_999_999);
    expect(centsFromDigits('999999999999')).toBe(MAX_AMOUNT_CENTS);
    expect(centsFromDigits('99999999999999999999')).toBe(MAX_AMOUNT_CENTS);
  });
});
