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
  acceptOpenFinance,
  declineAiChat,
  declineCapture,
  isCaptureAllowed,
  markCapturePromptShown,
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
  /**
   * A última leitura falhou (armazenamento seguro indisponível): a resposta do usuário é DESCONHECIDA. Não é
   * "nunca respondeu": nada é desligado nem perguntado por causa disso, e a leitura é tentada de novo.
   */
  loadFailed: boolean;
  /** Quantas leituras já foram tentadas para este usuário (muda a cada tentativa; agenda a próxima). */
  loadAttempts: number;
  record: ConsentRecord;
}

interface ConsentActions {
  /** Lê o registro do usuário (chamado ao entrar/abrir o app logado). */
  load: (userId: string) => Promise<void>;
  /**
   * As ações abaixo devolvem true só se o registro foi trocado E gravado. False (sem registro carregado, sessão de
   * outro usuário ou falha ao gravar) significa que NADA mudou: a tela deve ficar e avisar.
   */
  acceptCapture: () => Promise<boolean>;
  declineCapture: () => Promise<boolean>;
  markCapturePromptShown: () => Promise<boolean>;
  /** Interruptor das configurações. Sem aceite anterior não liga (a tela de consentimento deve ser mostrada). */
  setCaptureEnabled: (enabled: boolean) => Promise<boolean>;
  acceptAiChat: () => Promise<boolean>;
  declineAiChat: () => Promise<boolean>;
  /** Aceite do aviso de privacidade do Open Finance (passo 1 do wizard). */
  acceptOpenFinance: () => Promise<boolean>;
  reset: () => void;
}

const INITIAL: ConsentState = { userId: null, loaded: false, loadFailed: false, loadAttempts: 0, record: EMPTY_CONSENT };

export const useConsentStore = create<ConsentState & ConsentActions>((set, get) => {
  /** Troca o registro em memória na hora (síncrono) e só então grava; a gravação vai para a chave do dono do registro. */
  async function update(change: (record: ConsentRecord, nowIso: string) => ConsentRecord): Promise<boolean> {
    const { userId, loaded, record } = get();
    if (!userId || !loaded || userId !== useSessionStore.getState().userId) return false;
    const next = change(record, new Date().toISOString());
    if (next === record) return true; // nada a mudar (ex.: ligar sem aceite anterior): o chamador confere o estado
    set({ record: next });
    try {
      await SecureStore.setItemAsync(consentStorageKey(userId), serializeConsent(next));
      return true;
    } catch {
      // Não gravou: desfaz, para a tela não dizer que aceitou algo que não ficou registrado.
      if (get().userId === userId && get().record === next) set({ record });
      return false;
    }
  }

  return {
    ...INITIAL,

    load: async (userId) => {
      if (get().userId === userId && get().loaded) return;
      const attempts = get().userId === userId ? get().loadAttempts + 1 : 1;
      set({ userId, loaded: false, loadFailed: false, loadAttempts: attempts, record: EMPTY_CONSENT });
      let raw: string | null = null;
      let failed = false;
      try {
        raw = await SecureStore.getItemAsync(consentStorageKey(userId));
      } catch {
        failed = true;
      }
      // Saiu da conta (ou outro usuário entrou) durante a leitura: este resultado não vale.
      if (get().userId !== userId) return;
      if (failed) {
        // Não leu: continua "não carregado". Tratar como registro vazio desligaria a captura de quem já aceitou
        // e reabriria o pedido de consentimento.
        set({ loaded: false, loadFailed: true });
        return;
      }
      set({ loaded: true, loadFailed: false, record: parseConsent(raw) });
    },

    acceptCapture: () => update((r, now) => acceptCapture(r, now)),
    declineCapture: () => update((r, now) => declineCapture(r, now)),
    markCapturePromptShown: () => update((r, now) => markCapturePromptShown(r, now)),
    setCaptureEnabled: (enabled) => update((r, now) => setCaptureEnabled(r, enabled, now)),
    acceptAiChat: () => update((r, now) => acceptAiChat(r, now)),
    declineAiChat: () => update((r, now) => declineAiChat(r, now)),
    acceptOpenFinance: () => update((r, now) => acceptOpenFinance(r, now)),

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

registerUserDataCleaner(() => useConsentStore.getState().reset());
