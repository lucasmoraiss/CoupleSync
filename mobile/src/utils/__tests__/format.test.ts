import { monthLabelFromIso, memberLabel } from '../format';
import { getCategoryLabel } from '@/modules/transactions/categories';

describe('monthLabelFromIso', () => {
  it('usa o mês do período em UTC, não o do fuso do aparelho', () => {
    // O servidor devolve o início do mês em UTC. Em Brasília (UTC-3) esse instante ainda é
    // 30 de setembro às 21h, e o rótulo saía "setembro de 2026" durante todo o mês de outubro.
    expect(monthLabelFromIso('2026-10-01T00:00:00Z')).toBe('outubro de 2026');
  });

  it('aceita data sem hora e com fração de segundo', () => {
    expect(monthLabelFromIso('2026-01-01')).toBe('janeiro de 2026');
    expect(monthLabelFromIso('2026-12-01T00:00:00.000Z')).toBe('dezembro de 2026');
  });

  it('devolve texto vazio para valor que não é data', () => {
    expect(monthLabelFromIso('')).toBe('');
    expect(monthLabelFromIso('abc')).toBe('');
    expect(monthLabelFromIso('2026-13-01T00:00:00Z')).toBe('');
  });
});

describe('getCategoryLabel', () => {
  it('traduz a chave interna para o nome exibido', () => {
    expect(getCategoryLabel('ALIMENTACAO')).toBe('Alimentação');
    expect(getCategoryLabel('SAUDE')).toBe('Saúde');
  });

  it('reconhece a categoria em qualquer grafia vinda da API', () => {
    // O fluxo de caixa devolve "Alimentacao" e "Saude"; a classificação automática, "Alimentação".
    expect(getCategoryLabel('Alimentacao')).toBe('Alimentação');
    expect(getCategoryLabel('Saude')).toBe('Saúde');
    expect(getCategoryLabel('alimentação')).toBe('Alimentação');
    expect(getCategoryLabel(' Transporte ')).toBe('Transporte');
    expect(getCategoryLabel('outros')).toBe('Outros');
  });

  it('mantém o texto original quando a categoria não é conhecida', () => {
    expect(getCategoryLabel('Pets')).toBe('Pets');
  });
});

describe('memberLabel', () => {
  const membros = [
    { userId: 'u1', name: 'Ana Ribeiro' },
    { userId: 'u2', name: 'Bruno Martins' },
  ];

  it('mostra "Você" para o próprio usuário', () => {
    expect(memberLabel('u1', 'u1', membros)).toBe('Você');
  });

  it('mostra o primeiro nome do outro membro', () => {
    expect(memberLabel('u2', 'u1', membros)).toBe('Bruno');
  });

  it('usa "Membro" quando o nome ainda não foi carregado', () => {
    expect(memberLabel('u3', 'u1', membros)).toBe('Membro');
    expect(memberLabel('u2', 'u1', undefined)).toBe('Membro');
  });
});
