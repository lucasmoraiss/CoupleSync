// Consentimento do usuário logado, em memória, com cópia no armazenamento seguro POR USUÁRIO.
//
// Decisão: a chave inclui o id do usuário (couplesync_consent_<userId>). Sair da conta limpa só a memória
// (limpador registrado em userData.ts); o registro continua no aparelho, então o mesmo usuário que volta não
// é perguntado de novo, e OUTRO usuário no mesmo celular nunca enxerga nem herda o aceite do primeiro.
// Não há cópia no servidor: o registro local, com a data do aceite, é o comprovante. Reinstalar o app apaga o
// registro, e a captura volta a exigir o aceite.
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';
import { useSessionStore } from '@/state/sessionStore';
import { registerUserDataCleaner } from '@/state/userData';
import {
  EMPTY_CONSENT,
  acceptAiChat,
  acceptCapture,
  declineAiChat,
  declineCapture,
  isAiChatAllowed,
  isCaptureAllowed,
  parseConsent,
  serializeConsent,
  setCaptureEnabled,
  type ConsentRecord,
} from './consent';

const KEY_PREFIX = 'couplesync_consent_';

/** Chaves do armazenamento seguro aceitam só letras, números, ponto, hífen e sublinhado. */
export function consentStorageKey(userId: string): string {
  return `${KEY_PREFIX}${userId.replace(/[^A-Za-z0-9._-]/g, '_')}`;
}

interface ConsentState {
  /** Usuário a quem o registro em memória pertence. */
  userId: string | null;
  /** O registro do usuário já foi lido do armazenamento. Antes disso nada é permitido. */
  loaded: boolean;
  record: ConsentRecord;
}

interface ConsentActions {
  /** Lê o registro do usuário (chamado ao entrar/abrir o app logado). */
  load: (userId: string) => Promise<void>;
  acceptCapture: () => Promise<void>;
  declineCapture: () => Promise<void>;
  /** Interruptor das configurações. Sem aceite anterior não liga (a tela de consentimento deve ser mostrada). */
  setCaptureEnabled: (enabled: boolean) => Promise<void>;
  acceptAiChat: () => Promise<void>;
  declineAiChat: () => Promise<void>;
  reset: () => void;
}

const INITIAL: ConsentState = { userId: null, loaded: false, record: EMPTY_CONSENT };

export const useConsentStore = create<ConsentState & ConsentActions>((set, get) => {
  /** Troca o registro em memória na hora (síncrono) e só então grava; a gravação vai para a chave do dono do registro. */
  async function update(change: (record: ConsentRecord, nowIso: string) => ConsentRecord): Promise<void> {
    const { userId, loaded, record } = get();
    if (!userId || !loaded) return;
    const next = change(record, new Date().toISOString());
    if (next === record) return;
    set({ record: next });
    try {
      await SecureStore.setItemAsync(consentStorageKey(userId), serializeConsent(next));
    } catch {
      // Não gravou: vale na sessão atual; na próxima abertura o usuário será perguntado de novo (falha segura).
    }
  }

  return {
    ...INITIAL,

    load: async (userId) => {
      if (get().userId === userId && get().loaded) return;
      set({ userId, loaded: false, record: EMPTY_CONSENT });
      let raw: string | null = null;
      try {
        raw = await SecureStore.getItemAsync(consentStorageKey(userId));
      } catch {
        raw = null;
      }
      // Saiu da conta (ou outro usuário entrou) durante a leitura: este resultado não vale.
      if (get().userId !== userId) return;
      set({ loaded: true, record: parseConsent(raw) });
    },

    acceptCapture: () => update((r, now) => acceptCapture(r, now)),
    declineCapture: () => update((r, now) => declineCapture(r, now)),
    setCaptureEnabled: (enabled) => update((r, now) => setCaptureEnabled(r, enabled, now)),
    acceptAiChat: () => update((r, now) => acceptAiChat(r, now)),
    declineAiChat: () => update((r, now) => declineAiChat(r, now)),

    reset: () => set({ ...INITIAL }),
  };
});

/**
 * A captura de notificações pode enviar agora? Exige: registro carregado, dele ser do usuário logado,
 * aceite registrado e interruptor ligado. É a regra que o uploader consulta a cada evento.
 */
export function isCaptureAllowedNow(): boolean {
  const { userId, loaded, record } = useConsentStore.getState();
  const sessionUserId = useSessionStore.getState().userId;
  return loaded && userId !== null && userId === sessionUserId && isCaptureAllowed(record);
}

/** O chat com IA pode enviar dados ao Gemini agora? Mesma exigência de dono e carga. */
export function isAiChatAllowedNow(): boolean {
  const { userId, loaded, record } = useConsentStore.getState();
  const sessionUserId = useSessionStore.getState().userId;
  return loaded && userId !== null && userId === sessionUserId && isAiChatAllowed(record);
}

registerUserDataCleaner(() => useConsentStore.getState().reset());
