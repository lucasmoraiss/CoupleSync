// Progresso do wizard do Open Finance: do usuário logado, em memória, com cópia no armazenamento seguro numa
// chave POR USUÁRIO (couplesync_openfinance_wizard_<userId>).
//
// Decisões:
// - Só o passo e campos não sensíveis são guardados (ver serializeProgress). O Client ID, o Client Secret e os
//   Item IDs nunca saem da memória da tela: fechar o app no meio do passo 3 obriga a colar as credenciais de novo.
// - Sair da conta (ou a sessão acabar) apaga o progresso da memória E do aparelho: limpador registrado em
//   userData.ts. Outro usuário no mesmo celular nunca vê o progresso do primeiro.
// - Todo trabalho assíncrono fica preso à época da sessão: leitura ou gravação que termina depois de sair da
//   conta, de outro login ou da troca de grupo não vale e não deixa nada no aparelho.
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';
import { getSessionEpoch, useSessionStore } from '@/state/sessionStore';
import { registerUserDataCleaner } from '@/state/userData';
import { EMPTY_PROGRESS, parseProgress, progressStorageKey, serializeProgress, type WizardProgress } from './wizard';

interface WizardState {
  /** Usuário a quem o progresso em memória pertence. */
  userId: string | null;
  /** O progresso do usuário já foi lido do armazenamento. */
  loaded: boolean;
  progress: WizardProgress;
}

type ProgressChange = Partial<Pick<WizardProgress, 'step' | 'banksConnected' | 'connectionId'>>;

interface WizardActions {
  /** Lê o progresso do usuário e do grupo da sessão atual (chamado ao abrir o wizard). */
  load: () => Promise<void>;
  /**
   * Muda o progresso em memória na hora e grava. Devolve false se não ficou gravado (sem sessão, armazenamento
   * indisponível ou sessão trocada no meio): o wizard segue em memória, só não é retomado depois.
   */
  save: (change: ProgressChange) => Promise<boolean>;
  /** O wizard terminou: nada mais a retomar. */
  finish: () => Promise<void>;
  /** Só a memória (o armazenamento fica). */
  reset: () => void;
  /** Saída da conta: memória e armazenamento do usuário que estava logado. */
  clear: () => Promise<void>;
}

const INITIAL: WizardState = { userId: null, loaded: false, progress: EMPTY_PROGRESS };

// O último usuário logado: quando os limpadores rodam a sessão em memória já foi zerada, e o progresso pode
// estar no aparelho sem nunca ter sido lido nesta abertura do app.
let lastSignedInUserId: string | null = useSessionStore.getState().userId;
useSessionStore.subscribe((session) => {
  if (session.userId) lastSignedInUserId = session.userId;
});

async function removeStored(userId: string): Promise<void> {
  try {
    await SecureStore.deleteItemAsync(progressStorageKey(userId));
  } catch {
    // Não apagou: sobrescreve com algo que parseProgress descarta.
    try {
      await SecureStore.setItemAsync(progressStorageKey(userId), '{}');
    } catch {
      // nada mais a fazer
    }
  }
}

export const useWizardStore = create<WizardState & WizardActions>((set, get) => ({
  ...INITIAL,

  load: async () => {
    const { userId, coupleId } = useSessionStore.getState();
    if (!userId) return;
    const epoch = getSessionEpoch();
    set({ userId, loaded: false, progress: { ...EMPTY_PROGRESS, coupleId } });
    let raw: string | null = null;
    try {
      raw = await SecureStore.getItemAsync(progressStorageKey(userId));
    } catch {
      raw = null; // sem leitura, o wizard começa do início: nada sensível depende disso
    }
    // Saiu da conta, outro usuário entrou ou o grupo mudou durante a leitura: este resultado não vale.
    if (getSessionEpoch() !== epoch || get().userId !== userId) return;
    set({ loaded: true, progress: parseProgress(raw, coupleId) });
  },

  save: async (change) => {
    const { userId: sessionUserId, coupleId } = useSessionStore.getState();
    const { userId, loaded, progress } = get();
    if (!sessionUserId || !loaded || userId !== sessionUserId) return false;
    const epoch = getSessionEpoch();
    const next: WizardProgress = {
      ...progress,
      coupleId,
      step: change.step ?? progress.step,
      banksConnected: change.banksConnected ?? progress.banksConnected,
      connectionId: change.connectionId === undefined ? progress.connectionId : change.connectionId,
    };
    set({ progress: next });
    try {
      await SecureStore.setItemAsync(progressStorageKey(userId), serializeProgress(next));
    } catch {
      return false;
    }
    if (getSessionEpoch() !== epoch) {
      // A sessão acabou (ou mudou de grupo) enquanto gravava: o que acabou de ser escrito não pode ficar.
      await removeStored(userId);
      return false;
    }
    return true;
  },

  finish: async () => {
    const { userId } = get();
    const { coupleId } = useSessionStore.getState();
    if (!userId) return;
    set({ progress: { ...EMPTY_PROGRESS, coupleId } });
    await removeStored(userId);
  },

  reset: () => set({ ...INITIAL }),

  clear: async () => {
    const owner = get().userId ?? lastSignedInUserId;
    set({ ...INITIAL });
    if (owner) await removeStored(owner);
  },
}));

// Sair da conta (ou a sessão expirar) apaga o progresso do usuário que estava logado.
registerUserDataCleaner(() => useWizardStore.getState().clear());
