import {
  PREDEFINED_CATEGORIES,
  getCategoryIcon,
  getCategoryLabel,
  resolveCategories,
  toCategoryKey,
} from '../categories';

describe('lista embutida (reserva offline)', () => {
  it('tem as sete chaves canônicas da API, com OUTROS por último', () => {
    expect(PREDEFINED_CATEGORIES.map((c) => c.value)).toEqual([
      'ALIMENTACAO',
      'TRANSPORTE',
      'COMPRAS',
      'SAUDE',
      'LAZER',
      'MORADIA',
      'OUTROS',
    ]);
    expect(PREDEFINED_CATEGORIES.map((c) => c.label)).toEqual([
      'Alimentação',
      'Transporte',
      'Compras',
      'Saúde',
      'Lazer',
      'Moradia',
      'Outros',
    ]);
  });

  it('toda chave aparece em maiúsculas e sem acento', () => {
    for (const c of PREDEFINED_CATEGORIES) expect(c.value).toMatch(/^[A-Z]+$/);
  });
});

describe('toCategoryKey', () => {
  it.each([
    ['ALIMENTACAO', 'ALIMENTACAO'],
    ['Alimentação', 'ALIMENTACAO'],
    ['alimentacao', 'ALIMENTACAO'],
    ['  Saúde ', 'SAUDE'],
    ['outros', 'OUTROS'],
  ])('"%s" -> %s', (input, key) => {
    expect(toCategoryKey(input)).toBe(key);
  });

  it.each(['Mercado', 'Educação', '', '   ', null, undefined])('"%s" não é categoria', (input) => {
    expect(toCategoryKey(input as string | null | undefined)).toBeNull();
  });
});

describe('resolveCategories', () => {
  it('sem resposta da API usa a lista embutida', () => {
    expect(resolveCategories(undefined)).toBe(PREDEFINED_CATEGORIES);
    expect(resolveCategories(null)).toBe(PREDEFINED_CATEGORIES);
    expect(resolveCategories([])).toBe(PREDEFINED_CATEGORIES);
  });

  it('usa a lista da API e mantém o ícone conhecido de cada chave', () => {
    const result = resolveCategories([
      { key: 'ALIMENTACAO', label: 'Alimentação' },
      { key: 'EDUCACAO', label: 'Educação' },
    ]);

    expect(result).toEqual([
      { value: 'ALIMENTACAO', label: 'Alimentação', icon: 'restaurant-outline' },
      { value: 'EDUCACAO', label: 'Educação', icon: 'ellipsis-horizontal-circle-outline' },
    ]);
  });
});

describe('rótulos e ícones', () => {
  it('reconhecem qualquer grafia', () => {
    expect(getCategoryLabel('alimentacao')).toBe('Alimentação');
    expect(getCategoryIcon('SAUDE')).toBe('medkit-outline');
    expect(getCategoryLabel('Mercado')).toBe('Mercado');
  });
});
