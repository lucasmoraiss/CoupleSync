import { brazilToday, isDeadlineBeforeToday } from '../goalDeadline';

describe('brazilToday', () => {
  it('às 22h em Brasília ainda é o mesmo dia, embora em UTC já seja o seguinte', () => {
    expect(brazilToday(new Date('2026-10-16T01:00:00Z'))).toBe('2026-10-15');
  });

  it('vira o dia à meia-noite de Brasília, não à de UTC', () => {
    expect(brazilToday(new Date('2026-10-16T02:59:59Z'))).toBe('2026-10-15');
    expect(brazilToday(new Date('2026-10-16T03:00:00Z'))).toBe('2026-10-16');
  });

  it('não depende do fuso do aparelho', () => {
    const original = process.env.TZ;
    try {
      process.env.TZ = 'Asia/Tokyo';
      expect(brazilToday(new Date('2026-10-15T20:00:00Z'))).toBe('2026-10-15');
    } finally {
      if (original === undefined) delete process.env.TZ;
      else process.env.TZ = original;
    }
  });
});

describe('isDeadlineBeforeToday', () => {
  // 15/10 22:00 em Brasília (16/10 01:00 em UTC)
  const now = new Date('2026-10-16T01:00:00Z');

  it('o prazo de hoje (dia de Brasília) não está vencido', () => {
    expect(isDeadlineBeforeToday('2026-10-15T12:00:00.000Z', now)).toBe(false);
  });

  it('o prazo de ontem está vencido', () => {
    expect(isDeadlineBeforeToday('2026-10-14T12:00:00.000Z', now)).toBe(true);
  });

  it('o prazo de amanhã não está vencido', () => {
    expect(isDeadlineBeforeToday('2026-10-16T12:00:00.000Z', now)).toBe(false);
  });
});
