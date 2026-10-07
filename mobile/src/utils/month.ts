// Mês corrente no fuso do Brasil. O servidor decide o mês em America/Sao_Paulo (UTC-3, sem horário de verão);
// o aparelho usa o mesmo cálculo para que os dois nunca discordem, mesmo com o fuso do celular diferente.

const BRAZIL_OFFSET_MS = -3 * 60 * 60 * 1000;

/** "AAAA-MM" do mês em que o instante cai no horário de Brasília. */
export function brazilMonth(now: Date = new Date()): string {
  const local = new Date(now.getTime() + BRAZIL_OFFSET_MS);
  const year = local.getUTCFullYear();
  const month = String(local.getUTCMonth() + 1).padStart(2, '0');
  return `${year}-${month}`;
}
