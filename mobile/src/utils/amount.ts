// Validação de valores em reais, igual à da API: maior que zero, no máximo R$ 999.999.999,99 e no
// máximo duas casas decimais (nunca arredondar em silêncio). Lógica pura, coberta por testes.

/** Maior valor aceito pela API. */
export const MAX_AMOUNT = 999_999_999.99;

/** O mesmo teto em centavos, para os campos que digitam só números (cents). */
export const MAX_AMOUNT_CENTS = 99_999_999_999;

export const AMOUNT_MESSAGES = {
  invalid: 'Informe um valor válido (ex.: 42,90).',
  notPositive: 'Informe um valor maior que zero.',
  tooLarge: 'O valor máximo é R$ 999.999.999,99.',
  tooManyDecimals: 'Use no máximo duas casas decimais (ex.: 42,90).',
  negative: 'O valor não pode ser negativo.',
} as const;

export type AmountResult =
  | { readonly ok: true; readonly value: number }
  | { readonly ok: false; readonly message: string };

/** Quantidade de casas decimais de um texto numérico já normalizado ("12.345" -> 3). */
function decimalPlaces(normalized: string): number {
  const dot = normalized.indexOf('.');
  return dot === -1 ? 0 : normalized.length - dot - 1;
}

/**
 * Converte o texto digitado em número. Aceita "42,90", "42.90", "1.234,56", "R$ 42,90".
 * Com vírgula, o ponto é separador de milhar. Só com ponto: ponto seguido de exatamente três dígitos
 * ("1.234", "1.234.567") é milhar, como em pt-BR; ponto com um ou dois dígitos ("12.5") é decimal.
 * Não arredonda: "10,505" é rejeitado, não vira 10,51.
 */
export function parseAmountText(raw: string, options: { allowZero?: boolean } = {}): AmountResult {
  const cleaned = (raw ?? '').replace(/R\$/gi, '').replace(/\s/g, '');
  if (cleaned.length === 0) return { ok: false, message: AMOUNT_MESSAGES.invalid };
  if (cleaned.startsWith('-')) return { ok: false, message: AMOUNT_MESSAGES.negative };

  const thousandsOnly = /^[1-9]\d{0,2}(\.\d{3})+$/.test(cleaned);
  const normalized = cleaned.includes(',')
    ? cleaned.replace(/\./g, '').replace(',', '.')
    : thousandsOnly
      ? cleaned.replace(/\./g, '')
      : cleaned;

  if (!/^\d+(\.\d+)?$/.test(normalized)) return { ok: false, message: AMOUNT_MESSAGES.invalid };

  if (decimalPlaces(normalized) > 2) return { ok: false, message: AMOUNT_MESSAGES.tooManyDecimals };

  return validateAmount(Number(normalized), options);
}

/** Valida um valor já numérico (reais). */
export function validateAmount(value: number, options: { allowZero?: boolean } = {}): AmountResult {
  if (!Number.isFinite(value)) return { ok: false, message: AMOUNT_MESSAGES.invalid };
  if (value < 0) return { ok: false, message: AMOUNT_MESSAGES.negative };
  if (value === 0 && !options.allowZero) return { ok: false, message: AMOUNT_MESSAGES.notPositive };
  if (value > MAX_AMOUNT) return { ok: false, message: AMOUNT_MESSAGES.tooLarge };
  if (Math.round(value * 100) / 100 !== value) {
    return { ok: false, message: AMOUNT_MESSAGES.tooManyDecimals };
  }
  return { ok: true, value };
}

/** Valida um valor em centavos (campos que só aceitam dígitos): sempre tem duas casas. */
export function validateAmountCents(cents: number, options: { allowZero?: boolean } = {}): AmountResult {
  if (!Number.isFinite(cents) || !Number.isInteger(cents)) return { ok: false, message: AMOUNT_MESSAGES.invalid };
  if (cents < 0) return { ok: false, message: AMOUNT_MESSAGES.negative };
  if (cents === 0 && !options.allowZero) return { ok: false, message: AMOUNT_MESSAGES.notPositive };
  if (cents > MAX_AMOUNT_CENTS) return { ok: false, message: AMOUNT_MESSAGES.tooLarge };
  return { ok: true, value: cents / 100 };
}

/** Mensagem de erro de um valor em centavos, ou null quando é válido (para exibir sob o campo). */
export function amountCentsError(cents: number, options: { allowZero?: boolean } = {}): string | null {
  const result = validateAmountCents(cents, options);
  return result.ok ? null : result.message;
}

/**
 * Centavos a partir do texto de um campo "só dígitos" ("1.234,56" -> 123456). Limita ao teto da API
 * em vez de aceitar dígitos sem fim (que estourariam a precisão do número).
 */
export function centsFromDigits(raw: string): number {
  const digits = (raw ?? '').replace(/[^\d]/g, '');
  if (!digits) return 0;
  const cents = Number(digits.slice(0, 12));
  return Math.min(cents, MAX_AMOUNT_CENTS);
}
