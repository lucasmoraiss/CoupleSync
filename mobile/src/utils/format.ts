// Formatação de textos exibidos nas telas. Lógica pura, coberta por testes em __tests__/format.test.ts.

const MONTHS_PT_BR = [
  'janeiro', 'fevereiro', 'março', 'abril', 'maio', 'junho',
  'julho', 'agosto', 'setembro', 'outubro', 'novembro', 'dezembro',
] as const;

export interface MemberName {
  readonly userId: string;
  readonly name: string;
}

/**
 * Rótulo "mês de ano" de uma data ISO vinda do servidor, lido em UTC.
 * O servidor delimita o mês no horário de Brasília e devolve o início como instante UTC
 * (1º do mês às 03:00Z); ler o mês em UTC dá o mês certo, enquanto converter para o fuso do
 * aparelho deslocaria o início para o mês anterior em fusos a oeste de Greenwich.
 */
export function monthLabelFromIso(iso: string): string {
  const match = /^(\d{4})-(\d{2})/.exec(iso ?? '');
  if (!match) return '';
  const month = Number(match[2]);
  if (month < 1 || month > 12) return '';
  return `${MONTHS_PT_BR[month - 1]} de ${match[1]}`;
}

/** "Você" para o próprio usuário; o primeiro nome dos demais membros do grupo. */
export function memberLabel(
  userId: string,
  currentUserId: string | null,
  members: readonly MemberName[] | undefined,
): string {
  if (userId === currentUserId) return 'Você';
  const name = members?.find((m) => m.userId === userId)?.name?.trim();
  if (!name) return 'Membro';
  return name.split(/\s+/)[0];
}
