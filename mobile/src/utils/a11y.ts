// Acessibilidade: textos para leitores de tela (TalkBack). Lógica pura, coberta por testes em __tests__/a11y.test.ts.

/** Tamanho mínimo de alvo de toque (dp) recomendado pelo Android/WCAG. */
export const MIN_TOUCH_TARGET = 44;

/** Aumenta a área de toque de botões pequenos sem mudar o desenho (usar em hitSlop). */
export function hitSlopFor(size: number): { top: number; bottom: number; left: number; right: number } {
  const extra = Math.max(0, Math.ceil((MIN_TOUCH_TARGET - size) / 2));
  return { top: extra, bottom: extra, left: extra, right: extra };
}

/**
 * Valor em reais por extenso para o leitor de tela: "1234 reais e 50 centavos", "1 real", "50 centavos",
 * "zero reais", "menos 10 reais". Sem separador de milhar de propósito: "1.234" costuma ser lido como "1 ponto 234".
 */
export function spokenBRL(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '';
  const totalCents = Math.round(Math.abs(value) * 100);
  const reais = Math.floor(totalCents / 100);
  const cents = totalCents % 100;
  const sign = value < 0 && totalCents > 0 ? 'menos ' : '';

  const reaisText = `${reais} ${reais === 1 ? 'real' : 'reais'}`;
  const centsText = `${cents} ${cents === 1 ? 'centavo' : 'centavos'}`;

  if (reais === 0 && cents === 0) return 'zero reais';
  if (cents === 0) return `${sign}${reaisText}`;
  if (reais === 0) return `${sign}${centsText}`;
  return `${sign}${reaisText} e ${centsText}`;
}

/** "Meta tal: 500 reais de 1000 reais" etc. — junta o rótulo e o valor por extenso. */
export function spokenAmountLabel(label: string, value: number | null | undefined): string {
  const spoken = spokenBRL(value);
  return spoken ? `${label}: ${spoken}` : label;
}

export interface ChartSlice {
  readonly label: string;
  readonly value: number;
}

/**
 * Resumo textual de um gráfico de fatias ou barras, para quem não enxerga o desenho:
 * "Gastos por categoria, total 1000 reais. Alimentação: 450 reais (45%). Transporte: ...".
 * Mostra até `limit` itens e diz quantos ficaram de fora.
 */
export function describeSlices(title: string, slices: readonly ChartSlice[], options: { limit?: number; showPercent?: boolean } = {}): string {
  const { limit = 8, showPercent = true } = options;
  if (slices.length === 0) return `${title}: sem dados.`;
  const total = slices.reduce((sum, s) => sum + s.value, 0);
  const shown = slices.slice(0, limit).map((s) => {
    const percent = showPercent && total > 0 ? ` (${Math.round((s.value / total) * 100)}%)` : '';
    return `${s.label}: ${spokenBRL(s.value)}${percent}.`;
  });
  const rest = slices.length - shown.length;
  const totalText = showPercent ? `, total ${spokenBRL(total)}` : '';
  const more = rest > 0 ? ` Mais ${rest} ${rest === 1 ? 'item' : 'itens'}.` : '';
  return `${title}${totalText}. ${shown.join(' ')}${more}`;
}

/** Texto de uma barra de progresso: "Meta casa: 40% concluída, 400 reais de 1000 reais." */
export function describeProgress(title: string, percent: number, current?: number, target?: number): string {
  const pct = `${Math.round(Math.max(0, Math.min(100, percent)))}% concluída`;
  const amounts = current !== undefined && target !== undefined ? `, ${spokenBRL(current)} de ${spokenBRL(target)}` : '';
  return `${title}: ${pct}${amounts}.`;
}
