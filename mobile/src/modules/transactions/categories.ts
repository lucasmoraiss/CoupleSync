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

export function getCategoryLabel(value: string): string {
  return PREDEFINED_CATEGORIES.find((c) => c.value === value)?.label ?? value;
}

export function getCategoryIcon(value: string): string {
  return PREDEFINED_CATEGORIES.find((c) => c.value === value)?.icon ?? 'ellipsis-horizontal-circle-outline';
}
