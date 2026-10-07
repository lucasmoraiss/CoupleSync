import { formatBrazilDate, formatBrazilTime, parseBrazilDateTime } from '../brazilDateTime';

describe('formatBrazilDate / formatBrazilTime', () => {
  it('mostra o dia e a hora de Brasília, não os de UTC', () => {
    expect(formatBrazilDate('2026-10-16T01:30:00Z')).toBe('15/10/2026');
    expect(formatBrazilTime('2026-10-16T01:30:00Z')).toBe('22:30');
  });

  it('não depende do fuso do aparelho', () => {
    const original = process.env.TZ;
    try {
      process.env.TZ = 'Asia/Tokyo';
      expect(formatBrazilDate('2026-10-16T01:30:00Z')).toBe('15/10/2026');
    } finally {
      if (original === undefined) delete process.env.TZ;
      else process.env.TZ = original;
    }
  });
});

describe('parseBrazilDateTime', () => {
  it('converte data e hora de Brasília em instante UTC', () => {
    expect(parseBrazilDateTime('15/10/2026', '22:30')).toEqual({ ok: true, iso: '2026-10-16T01:30:00.000Z' });
  });

  it('ida e volta com o formatador', () => {
    const result = parseBrazilDateTime('01/03/2026', '08:05');
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(formatBrazilDate(result.iso)).toBe('01/03/2026');
      expect(formatBrazilTime(result.iso)).toBe('08:05');
    }
  });

  it('fim de mês à noite em Brasília já é o mês seguinte em UTC, e volta certo', () => {
    const result = parseBrazilDateTime('31/10/2026', '23:30');
    expect(result).toEqual({ ok: true, iso: '2026-11-01T02:30:00.000Z' });
    if (result.ok) {
      expect(formatBrazilDate(result.iso)).toBe('31/10/2026');
      expect(formatBrazilTime(result.iso)).toBe('23:30');
    }
  });

  it('aceita dia e mês com um dígito', () => {
    expect(parseBrazilDateTime('5/3/2026', '9:00').ok).toBe(true);
  });

  it.each([['31/02/2026'], ['00/10/2026'], ['10/13/2026'], ['2026-10-10'], [''], ['10/10/1999']])(
    'rejeita a data %s',
    (date) => {
      expect(parseBrazilDateTime(date, '10:00').ok).toBe(false);
    },
  );

  it.each([['24:00'], ['10:60'], ['1000'], ['']])('rejeita o horário %s', (time) => {
    expect(parseBrazilDateTime('10/10/2026', time).ok).toBe(false);
  });
});
