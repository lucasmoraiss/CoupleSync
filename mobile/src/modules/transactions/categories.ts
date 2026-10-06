// Lista de categorias do app. A API é a dona da lista (GET /api/v1/categories); esta cópia é o
// reserva offline e é travada contra a da API por um teste do back-end (TransactionCategories).

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

/** A chave canônica ("ALIMENTACAO") de qualquer grafia ("Alimentação", " alimentacao "), ou null. */
export function toCategoryKey(value: string | null | undefined): string | null {
  return findCategory(value ?? '')?.value ?? null;
}

/** Categoria como a API devolve em GET /api/v1/categories. */
export interface ApiCategory {
  readonly key: string;
  readonly label: string;
}

const DEFAULT_ICON = 'ellipsis-horizontal-circle-outline';

/**
 * Lista a mostrar nos seletores: a da API (com o ícone conhecido de cada chave) ou, sem resposta
 * (offline, erro, lista vazia), a lista embutida.
 */
export function resolveCategories(fromApi: readonly ApiCategory[] | null | undefined): readonly CategoryMeta[] {
  if (!fromApi || fromApi.length === 0) return PREDEFINED_CATEGORIES;
  return fromApi.map((c) => ({
    value: c.key,
    label: c.label,
    icon: PREDEFINED_CATEGORIES.find((p) => p.value === c.key)?.icon ?? DEFAULT_ICON,
  }));
}

export function getCategoryLabel(value: string): string {
  return findCategory(value)?.label ?? value;
}

export function getCategoryIcon(value: string): string {
  return findCategory(value)?.icon ?? DEFAULT_ICON;
}
