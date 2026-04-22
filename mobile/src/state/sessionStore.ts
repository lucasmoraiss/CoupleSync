// AC-011: Zustand session store persisted to expo-secure-store
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';

const SECURE_STORE_KEY = 'couplesync_session';

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
  /** Update the persisted access token and couple id atomically (used after create/join couple). */
  setAccessTokenAndCouple: (accessToken: string, coupleId: string) => Promise<void>;
  clearSession: () => Promise<void>;
  hydrateFromStore: () => Promise<void>;
}

type SessionStore = SessionState & SessionActions;

export const useSessionStore = create<SessionStore>((set, get) => ({
  accessToken: null,
  refreshToken: null,
  userId: null,
  coupleId: null,

  setSession: async (accessToken, refreshToken, userId, coupleId) => {
    const payload: SessionState = { accessToken, refreshToken, userId, coupleId };
    await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify(payload));
    set(payload);
  },

  setCoupleId: async (coupleId: string) => {
    const state = get();
    const payload: SessionState = { ...state, coupleId };
    await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
      accessToken: payload.accessToken,
      refreshToken: payload.refreshToken,
      userId: payload.userId,
      coupleId: payload.coupleId,
    }));
    set({ coupleId });
  },

  setAccessTokenAndCouple: async (accessToken: string, coupleId: string) => {
    const state = get();
    const payload: SessionState = { ...state, accessToken, coupleId };
    await SecureStore.setItemAsync(SECURE_STORE_KEY, JSON.stringify({
      accessToken: payload.accessToken,
      refreshToken: payload.refreshToken,
      userId: payload.userId,
      coupleId: payload.coupleId,
    }));
    set({ accessToken, coupleId });
  },

  clearSession: async () => {
    await SecureStore.deleteItemAsync(SECURE_STORE_KEY);
    set({ accessToken: null, refreshToken: null, userId: null, coupleId: null });
  },

  hydrateFromStore: async () => {
    const raw = await SecureStore.getItemAsync(SECURE_STORE_KEY);
    if (raw) {
      try {
        const parsed = JSON.parse(raw);
        // Validate minimum required fields before trusting the payload
        if (
          typeof parsed?.accessToken === 'string' && parsed.accessToken &&
          typeof parsed?.refreshToken === 'string' && parsed.refreshToken &&
          typeof parsed?.userId === 'string' && parsed.userId
        ) {
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
}));
