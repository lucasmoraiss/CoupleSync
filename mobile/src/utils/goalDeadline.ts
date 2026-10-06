// "Hoje" do prazo de uma meta é o dia de Brasília (UTC-3, sem horário de verão), o mesmo que o servidor usa,
// e não o dia do aparelho nem o de UTC. O prazo é uma data: o dia dela é a parte de data do instante UTC enviado
// pelo app (meio-dia UTC do dia escolhido).

const BRAZIL_OFFSET_MS = -3 * 60 * 60 * 1000;

/** "AAAA-MM-DD" do dia em que o instante cai no horário de Brasília. */
export function brazilToday(now: Date = new Date()): string {
  return new Date(now.getTime() + BRAZIL_OFFSET_MS).toISOString().slice(0, 10);
}

/** O dia do prazo já passou? (O próprio dia do prazo ainda não está vencido.) */
export function isDeadlineBeforeToday(deadlineIso: string, now: Date = new Date()): boolean {
  const deadlineDay = new Date(deadlineIso).toISOString().slice(0, 10);
  return deadlineDay < brazilToday(now);
}
