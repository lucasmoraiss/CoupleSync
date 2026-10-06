// AC-011: Zustand session store persisted to expo-secure-store
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';

const SECURE_STORE_KEY = 'couplesync_session';

// Época da sessão: sobe quando uma sessão começa (login/hidratação) ou termina (logout/expiração).
// Quem grava de forma assíncrona (armazenamento seguro) anota a época antes e confere depois: se mudou,
// o resultado é de uma sessão que já não existe e não pode voltar para a memória nem para o armazenamento.
// Também serve ao cliente HTTP para não repetir, com o token de outro usuário, uma requisição de uma sessão anterior.
let sessionEpoch = 0;

export function getSessionEpoch(): number {
  return sessionEpoch;
}

interface SessionState {
  accessToken: string | null;
  refreshToken: string | null;
  userId: string | null;
  coupleId: string | null;
}

interface SessionActions {
  setSession: (
    accessToken: string,
    refreshToken: string,
    userId: string,
    coupleId: string | null
  ) => Promise<void>;
  setCoupleId: (coupleId: string) => Promise<void>;
  /**
   * Update the persisted access token and couple id atomically (used after create/join couple).
   * The API also returns a refresh token when the user had none (e.g. after being removed from a group):
   * when present it replaces the stored one; when absent the stored one is kept.
   */
  setAccessTokenAndCouple: (accessToken: string, coupleId: string, refreshToken?: string | null) => Promise<void>;
  /** The server says the user has no group (anymore): forget the stale group id, keep the session. */
  clearCouple: () => Promise<void>;
  /** Replace the token pair after a refresh, keeping userId and coupleId. No-op if there is no session. */
  setTokens: (accessToken: string, refreshToken: string) => Promise<void>;
  /**
   * The active group changed (switched, left into another group, created or joined another one): store the
   * tokens of the new group. Counts as a new session for requests in flight: one sent on behalf of the previous
   * group is never repeated with the new group's token. A missing refresh token keeps the stored one.
   */
  setActiveGroup: (accessToken: string, coupleId: string | null, refreshToken?: string | null) => Promise<void>;
  /** After leaving the group: store the group-less token pair and forget the group. */
  leaveCouple: (accessToken: string, refreshToken: string) => Promise<void>;
  clearSession: () => Promise<void>;
  hydrateFromStore: () => Promise<void>;
}

type SessionStore = SessionState & SessionActions;

export const useSessionStore = create<SessionStore>((set, get) => {
  /**
   * Depois de uma gravação assíncrona: devolve true se a sessão ainda é a mesma. Se mudou (logout ou outro login
   * durante a espera), a gravação que acabou de terminar é desfeita — apaga o armazenamento, ou o reescreve com a
   * sessão que está valendo agora — e devolve false (o chamador não toca na memória).
   */
  async function stillSameSession(epoch: number): Promise<boolean> {
    if (epoch === sessionEpoch) return true;
    const { accessToken, refreshToken, userId, coupleId } = get();
    try {
      if (accessToken && refreshToken && userId) {
        await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({ accessToken, refreshToken, userId, coupleId }));
      } else {
        await SecureStore.deleteItemAsync(SECURE_STORE_KEY);
      }
    } catch {
      try {
        await SecureStore.setItemAsync(SECURE_STORE_KEY, '{}');
      } catch {
        // nada mais a fazer
      }
    }
    return false;
  }

  return {
    accessToken: null,
    refreshToken: null,
    userId: null,
    coupleId: null,

    setSession: async (accessToken, refreshToken, userId, coupleId) => {
      const payload: SessionState = { accessToken, refreshToken, userId, coupleId };
      const epoch = ++sessionEpoch;
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify(payload));
      if (!(await stillSameSession(epoch))) return; // saiu (ou outro login) durante a gravação
      set(payload);
    },

    setCoupleId: async (coupleId: string) => {
      const state = get();
      if (!state.userId) return;
      const epoch = sessionEpoch;
      const payload: SessionState = { ...state, coupleId };
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
        accessToken: payload.accessToken,
        refreshToken: payload.refreshToken,
        userId: payload.userId,
        coupleId: payload.coupleId,
      }));
      if (!(await stillSameSession(epoch))) return;
      set({ coupleId });
    },

    setAccessTokenAndCouple: async (accessToken: string, coupleId: string, newRefreshToken?: string | null) => {
      const state = get();
      if (!state.userId) return;
      const epoch = sessionEpoch;
      const refreshToken = newRefreshToken || state.refreshToken;
      const payload: SessionState = { ...state, accessToken, refreshToken, coupleId };
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
        accessToken: payload.accessToken,
        refreshToken: payload.refreshToken,
        userId: payload.userId,
        coupleId: payload.coupleId,
      }));
      if (!(await stillSameSession(epoch))) return;
      set({ accessToken, refreshToken, coupleId });
    },

    clearCouple: async () => {
      const state = get();
      // No session (already signed out) or no stale group to forget: nothing to write.
      if (!state.userId || state.coupleId === null) return;
      const epoch = sessionEpoch;
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
        accessToken: state.accessToken,
        refreshToken: state.refreshToken,
        userId: state.userId,
        coupleId: null,
      }));
      if (!(await stillSameSession(epoch))) return;
      set({ coupleId: null });
    },

    setTokens: async (accessToken: string, refreshToken: string) => {
      const state = get();
      // Session was cleared (logout) while the refresh was in flight — do not resurrect it
      if (!state.userId) return;
      const epoch = sessionEpoch;
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
        accessToken,
        refreshToken,
        userId: state.userId,
        coupleId: state.coupleId,
      }));
      // Se o logout chegou durante a gravação, os tokens de quem saiu não voltam (nem ficam no armazenamento).
      if (!(await stillSameSession(epoch))) return;
      set({ accessToken, refreshToken });
    },

    setActiveGroup: async (accessToken: string, coupleId: string | null, newRefreshToken?: string | null) => {
      const state = get();
      if (!state.userId) return;
      const epoch = sessionEpoch;
      const refreshToken = newRefreshToken || state.refreshToken;
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
        accessToken,
        refreshToken,
        userId: state.userId,
        coupleId,
      }));
      if (!(await stillSameSession(epoch))) return;
      // Época e tokens mudam juntos: daqui em diante toda requisição já sai com o token do grupo novo.
      sessionEpoch += 1;
      set({ accessToken, refreshToken, coupleId });
    },

    leaveCouple: async (accessToken: string, refreshToken: string) => {
      const state = get();
      if (!state.userId) return;
      const epoch = sessionEpoch;
      await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
        accessToken,
        refreshToken,
        userId: state.userId,
        coupleId: null,
      }));
      if (!(await stillSameSession(epoch))) return;
      set({ accessToken, refreshToken, coupleId: null });
    },

    clearSession: async () => {
      // Memória primeiro e de forma síncrona: a partir daqui nenhuma requisição leva o token de quem saiu e
      // nenhum refresh em andamento consegue gravar tokens (setTokens exige userId). O armazenamento seguro
      // é nativo e assíncrono; esperá-lo antes deixaria as telas montadas com o token vivo.
      sessionEpoch += 1;
      set({ accessToken: null, refreshToken: null, userId: null, coupleId: null });
      try {
        await SecureStore.deleteItemAsync(SECURE_STORE_KEY);
      } catch {
        // Não conseguiu apagar: sobrescreve com um valor inválido, que hydrateFromStore descarta, para
        // que a sessão não volte na próxima abertura do app. Se nem isso der, a saída em memória vale.
        try {
          await SecureStore.setItemAsync(SECURE_STORE_KEY, '{}');
        } catch {
          // nada mais a fazer
        }
      }
    },

    hydrateFromStore: async () => {
      const epoch = sessionEpoch;
      const raw = await SecureStore.getItemAsync(SECURE_STORE_KEY);
      // Login ou logout durante a leitura: o que estava guardado já não vale.
      if (epoch !== sessionEpoch) return;
      if (raw) {
        try {
          const parsed = JSON.parse(raw);
          // Validate minimum required fields before trusting the payload
          if (
            typeof parsed?.accessToken === 'string' && parsed.accessToken &&
            typeof parsed?.refreshToken === 'string' && parsed.refreshToken &&
            typeof parsed?.userId === 'string' && parsed.userId
          ) {
            sessionEpoch += 1;
            set({
              accessToken: parsed.accessToken,
              refreshToken: parsed.refreshToken,
              userId: parsed.userId,
              coupleId: typeof parsed?.coupleId === 'string' ? parsed.coupleId : null,
            });
          } else {
            await SecureStore.deleteItemAsync(SECURE_STORE_KEY);
          }
        } catch {
          await SecureStore.deleteItemAsync(SECURE_STORE_KEY);
        }
      }
    },
};
});
