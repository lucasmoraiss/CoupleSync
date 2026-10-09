// "Agora não" no aviso de versão nova: guarda a última versão dispensada, em memória e no armazenamento seguro.
//
// Decisão: isto é POR APARELHO, não por usuário. O aviso fala do APK instalado neste celular, não da conta de
// quem está logado. Por isso a chave é fixa (sem id de usuário), este módulo NÃO se registra em
// state/userData.ts e sair da conta não apaga nada daqui: quem dispensou a versão X não volta a ver o aviso dela
// só porque saiu e entrou de novo. Não há dado pessoal: o valor guardado é só um número de versão.
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';
import type { DismissalState } from './appUpdate';

export const DISMISSED_VERSION_KEY = 'couplesync_app_update_dismissed';

interface DismissalActions {
  /** Lê o que foi dispensado neste aparelho. Armazenamento indisponível conta como "nada dispensado". */
  load: () => Promise<void>;
  /** Esconde o aviso para `version` na hora e grava. Se não gravar, vale só enquanto o app estiver aberto. */
  dismiss: (version: string | null) => Promise<void>;
  resetForTests: () => void;
}

const INITIAL: DismissalState = { loaded: false, dismissedVersion: null };

export const useAppUpdateDismissal = create<DismissalState & DismissalActions>((set, get) => ({
  ...INITIAL,

  load: async () => {
    if (get().loaded) return;
    let stored: string | null = null;
    try {
      stored = await SecureStore.getItemAsync(DISMISSED_VERSION_KEY);
    } catch {
      // Não leu: segue como "nada dispensado" (o pior que acontece é o aviso aparecer de novo).
    }
    // Um "Agora não" dado enquanto a leitura corria é mais novo do que o que estava gravado.
    if (get().loaded) return;
    set({ loaded: true, dismissedVersion: stored });
  },

  dismiss: async (version) => {
    if (!version) return;
    set({ loaded: true, dismissedVersion: version });
    try {
      await SecureStore.setItemAsync(DISMISSED_VERSION_KEY, version);
    } catch {
      // Não gravou: o aviso fica escondido até o app ser fechado.
    }
  },

  resetForTests: () => set({ ...INITIAL }),
}));
