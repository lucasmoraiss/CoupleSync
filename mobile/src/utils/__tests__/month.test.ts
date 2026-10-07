import { brazilMonth } from '../month';

describe('brazilMonth', () => {
  it('uma compra às 22h30 do dia 31 em Brasília ainda é do mês, embora em UTC já seja o dia 1', () => {
    expect(brazilMonth(new Date('2026-11-01T01:30:00Z'))).toBe('2026-10');
  });

  it('vira o mês à meia-noite de Brasília, não à de UTC', () => {
    expect(brazilMonth(new Date('2026-11-01T02:59:59Z'))).toBe('2026-10');
    expect(brazilMonth(new Date('2026-11-01T03:00:00Z'))).toBe('2026-11');
  });

  it('atravessa o ano', () => {
    expect(brazilMonth(new Date('2027-01-01T02:00:00Z'))).toBe('2026-12');
    expect(brazilMonth(new Date('2027-01-01T03:00:00Z'))).toBe('2027-01');
  });

  it('não depende do fuso do aparelho', () => {
    const original = process.env.TZ;
    try {
      process.env.TZ = 'Asia/Tokyo';
      expect(brazilMonth(new Date('2026-10-31T20:00:00Z'))).toBe('2026-10');
    } finally {
      if (original === undefined) delete process.env.TZ;
      else process.env.TZ = original;
    }
  });
});
