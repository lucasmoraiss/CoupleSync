// AC-003: Predefined transaction categories matching backend CategoryRulesSeeder values

export interface CategoryMeta {
  readonly value: string;
  readonly label: string;
  readonly icon: string;
}

export const PREDEFINED_CATEGORIES: readonly CategoryMeta[] = [
  { value: 'ALIMENTACAO', label: 'Alimentação', icon: 'restaurant-outline' },
  { value: 'TRANSPORTE', label: 'Transporte', icon: 'car-outline' },
  { value: 'COMPRAS', label: 'Compras', icon: 'bag-handle-outline' },
  { value: 'SAUDE', label: 'Saúde', icon: 'medkit-outline' },
  { value: 'LAZER', label: 'Lazer', icon: 'game-controller-outline' },
  { value: 'MORADIA', label: 'Moradia', icon: 'home-outline' },
  { value: 'OUTROS', label: 'Outros', icon: 'ellipsis-horizontal-circle-outline' },
];

// A API devolve a mesma categoria em grafias diferentes ("ALIMENTACAO", "Alimentacao",
// "Alimentação"); a comparação ignora maiúsculas, acentos e espaços nas pontas.
function normalizeCategory(value: string): string {
  return value
    .trim()
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toUpperCase();
}

function findCategory(value: string): CategoryMeta | undefined {
  const key = normalizeCategory(value ?? '');
  return PREDEFINED_CATEGORIES.find((c) => c.value === key);
}

export function getCategoryLabel(value: string): string {
  return findCategory(value)?.label ?? value;
}

export function getCategoryIcon(value: string): string {
  return findCategory(value)?.icon ?? 'ellipsis-horizontal-circle-outline';
}
