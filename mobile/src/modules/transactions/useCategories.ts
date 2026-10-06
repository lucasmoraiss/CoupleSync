// Categorias dos seletores: vêm da API (lista canônica) e, se ela não responder, da lista embutida.
import { useQuery } from '@tanstack/react-query';
import { categoriesApiClient } from '@/services/apiClient';
import { resolveCategories, type CategoryMeta } from './categories';

export function useCategories(): readonly CategoryMeta[] {
  const { data } = useQuery({
    queryKey: ['categories'],
    queryFn: async () => (await categoriesApiClient.list()).data.categories,
    staleTime: 24 * 60 * 60 * 1000,
    retry: false,
  });

  return resolveCategories(data);
}
