// Liga `activateGroup` às peças reais do app. Único ponto por onde o grupo ativo muda no aparelho.
import type { QueryClient } from '@tanstack/react-query';
import { clearGroupScopedQueries, queryClient } from '@/services/queryClient';
import { useSessionStore } from '@/state/sessionStore';
import { useDashboardStore } from '@/state/dashboardStore';
import { useGroupEpoch } from '@/state/groupEpoch';
import { clearPendingEvents } from '@/modules/integrations/notification-capture/eventUploader';
import { activateGroup, type ActivateGroupDeps, type GroupSession } from './activateGroup';

export const MY_GROUPS_QUERY_KEY = ['my-groups'] as const;
const GROUP_QUERY_KEY = ['couple-me'] as const;

export function groupSessionDeps(client: QueryClient): ActivateGroupDeps {
  return {
    dropPendingCaptures: clearPendingEvents,
    saveSession: (session) =>
      useSessionStore.getState().setActiveGroup(session.accessToken, session.coupleId, session.refreshToken),
    cancelQueries: () => client.cancelQueries(),
    clearGroupData: () => {
      clearGroupScopedQueries(client);
      // O helper preserva a consulta do próprio grupo (para quem ficou sem grupo); aqui o grupo mudou, então ela
      // também é do grupo anterior e sai.
      client.removeQueries({ queryKey: GROUP_QUERY_KEY });
      useDashboardStore.getState().resetToCurrentMonth();
    },
    remountScreens: () => useGroupEpoch.getState().bump(),
  };
}

/** Passa o aparelho para o grupo desta sessão (tokens devolvidos por trocar, sair, criar ou entrar). */
export function applyGroupSession(session: GroupSession): Promise<void> {
  return activateGroup(session, groupSessionDeps(queryClient));
}
