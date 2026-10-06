// Data e hora de uma transação são digitadas e mostradas no horário de Brasília (UTC-3, sem horário de verão),
// o mesmo calendário que o servidor usa, e enviadas à API como instante UTC, como na criação da transação.

const BRAZIL_OFFSET_MS = -3 * 60 * 60 * 1000;

export type BrazilDateTimeResult =
  | { readonly ok: true; readonly iso: string }
  | { readonly ok: false; readonly message: string };

export const DATE_TIME_MESSAGES = {
  invalidDate: 'Informe uma data válida (dd/mm/aaaa).',
  invalidTime: 'Informe um horário válido (hh:mm).',
} as const;

function pad(value: number): string {
  return String(value).padStart(2, '0');
}

function toBrazilParts(iso: string) {
  const local = new Date(new Date(iso).getTime() + BRAZIL_OFFSET_MS);
  return {
    day: local.getUTCDate(),
    month: local.getUTCMonth() + 1,
    year: local.getUTCFullYear(),
    hour: local.getUTCHours(),
    minute: local.getUTCMinutes(),
  };
}

/** "dd/mm/aaaa" do dia de Brasília em que o instante UTC cai. */
export function formatBrazilDate(iso: string): string {
  const p = toBrazilParts(iso);
  return `${pad(p.day)}/${pad(p.month)}/${p.year}`;
}

/** "hh:mm" do horário de Brasília do instante UTC. */
export function formatBrazilTime(iso: string): string {
  const p = toBrazilParts(iso);
  return `${pad(p.hour)}:${pad(p.minute)}`;
}

/** Data ("dd/mm/aaaa") e hora ("hh:mm") de Brasília -> instante UTC em ISO. */
export function parseBrazilDateTime(dateText: string, timeText: string): BrazilDateTimeResult {
  const date = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec((dateText ?? '').trim());
  if (!date) return { ok: false, message: DATE_TIME_MESSAGES.invalidDate };
  const time = /^(\d{1,2}):(\d{2})$/.exec((timeText ?? '').trim());
  if (!time) return { ok: false, message: DATE_TIME_MESSAGES.invalidTime };

  const day = Number(date[1]);
  const month = Number(date[2]);
  const year = Number(date[3]);
  const hour = Number(time[1]);
  const minute = Number(time[2]);

  if (year < 2000 || month < 1 || month > 12 || day < 1) return { ok: false, message: DATE_TIME_MESSAGES.invalidDate };
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate();
  if (day > daysInMonth) return { ok: false, message: DATE_TIME_MESSAGES.invalidDate };
  if (hour > 23 || minute > 59) return { ok: false, message: DATE_TIME_MESSAGES.invalidTime };

  const utcMs = Date.UTC(year, month - 1, day, hour, minute) - BRAZIL_OFFSET_MS;
  return { ok: true, iso: new Date(utcMs).toISOString() };
}
