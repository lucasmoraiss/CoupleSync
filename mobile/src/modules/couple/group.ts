// Regras de apresentação da tela do grupo. Lógica pura, coberta por testes em __tests__/group.test.ts.

const DAY_MS = 24 * 60 * 60 * 1000;

export interface JoinCodeValidity {
  readonly expired: boolean;
  readonly text: string;
}

/** Instante UTC de uma data do servidor, mesmo quando vem sem marcador de fuso. */
function parseUtc(iso: string): number {
  const hasZone = /(Z|[+-]\d{2}:?\d{2})$/i.test(iso);
  return new Date(hasZone ? iso : `${iso}Z`).getTime();
}

/** Texto de validade do código de convite ("Vale por mais 3 dias", "Vence hoje", "Vencido"). */
export function describeJoinCodeValidity(expiresAtUtc: string, now: Date = new Date()): JoinCodeValidity {
  const expiresAt = parseUtc(expiresAtUtc);
  if (Number.isNaN(expiresAt)) return { expired: false, text: '' };

  const remaining = expiresAt - now.getTime();
  if (remaining <= 0) return { expired: true, text: 'Vencido. Gere um código novo para convidar alguém.' };

  const days = Math.ceil(remaining / DAY_MS);
  if (days <= 1) return { expired: false, text: 'Vence hoje' };
  return { expired: false, text: `Vale por mais ${days} dias` };
}

/** O dono do grupo é quem pode remover membros e gerar um novo código. */
export function isGroupOwner(ownerUserId: string | null | undefined, userId: string | null): boolean {
  return !!ownerUserId && !!userId && ownerUserId === userId;
}
