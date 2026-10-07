// Os grupos do usuário logado. Num servidor que ainda não tem a rota (ou sem rede) a consulta falha em silêncio:
// o seletor de grupo simplesmente não aparece e o app segue com um grupo só, como antes.
import { useQuery } from '@tanstack/react-query';
import { coupleApiClient } from '@/services/apiClient';
import { useSessionStore } from '@/state/sessionStore';
import type { MyGroupsResponse } from '@/types/api';
import { MY_GROUPS_QUERY_KEY } from './groupSession';

export function useMyGroups() {
  const signedIn = useSessionStore((state) => !!state.accessToken);
  return useQuery<MyGroupsResponse>({
    queryKey: MY_GROUPS_QUERY_KEY,
    queryFn: () => coupleApiClient.listMine().then((response) => response.data),
    enabled: signedIn,
    retry: false,
  });
}
