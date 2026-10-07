import { describeJoinCodeValidity, isGroupOwner } from '../group';

const NOW = new Date('2026-10-06T12:00:00Z');

describe('describeJoinCodeValidity', () => {
  it('mostra os dias que faltam, arredondando para cima', () => {
    expect(describeJoinCodeValidity('2026-10-13T12:00:00Z', NOW)).toEqual({ expired: false, text: 'Vale por mais 7 dias' });
    expect(describeJoinCodeValidity('2026-10-09T11:00:00Z', NOW)).toEqual({ expired: false, text: 'Vale por mais 3 dias' });
  });

  it('no último dia avisa que vence hoje', () => {
    expect(describeJoinCodeValidity('2026-10-07T11:59:00Z', NOW)).toEqual({ expired: false, text: 'Vence hoje' });
  });

  it('código vencido pede um novo', () => {
    const result = describeJoinCodeValidity('2026-10-06T12:00:00Z', NOW);
    expect(result.expired).toBe(true);
    expect(result.text).toMatch(/Gere um código novo/);
  });

  it('lê como UTC uma data sem marcador de fuso', () => {
    expect(describeJoinCodeValidity('2026-10-13T12:00:00', NOW).text).toBe('Vale por mais 7 dias');
  });

  it('data ilegível não quebra a tela', () => {
    expect(describeJoinCodeValidity('quando?', NOW)).toEqual({ expired: false, text: '' });
  });
});

describe('isGroupOwner', () => {
  it('só o dono registrado é dono', () => {
    expect(isGroupOwner('u1', 'u1')).toBe(true);
    expect(isGroupOwner('u1', 'u2')).toBe(false);
    expect(isGroupOwner(null, 'u1')).toBe(false);
    expect(isGroupOwner('u1', null)).toBe(false);
  });
});
