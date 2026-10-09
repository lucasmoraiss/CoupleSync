// A última resposta do servidor sobre versões (GET /api/v1/app/version), lembrada NESTE APARELHO.
//
// Para que serve: o app nunca espera a rede para abrir. Um aparelho que já se soube bloqueado (versão instalada
// abaixo da mínima) abre direto na tela de bloqueio, sem montar nenhuma aba, e reconfere em segundo plano. A regra
// (appUpdate.ts, resolveUpdate) compara sempre com a versão instalada AGORA: quem atualizou o APK destrava na
// hora, mesmo com a lembrança antiga. Lembrança ausente ou ilegível = abre normal.
//
// Como o "Agora não" (dismissalStore.ts), isto é POR APARELHO, não por usuário: fala do APK instalado, o valor
// são só dois números de versão, a chave é fixa, este módulo NÃO se registra em state/userData.ts e sair da
// conta não apaga nada daqui.
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';
import { parseRememberedAnswer, serializeAnswer, type RememberedAnswer, type UpdateMemory } from './appUpdate';

export const REMEMBERED_ANSWER_KEY = 'couplesync_app_update_last_answer';

/** O layout espera esta leitura (local) antes de montar as abas; se o armazenamento não responder, segue sem ela. */
export const MEMORY_LOAD_TIMEOUT_MS = 2000;

interface MemoryActions {
  /** Lê a lembrança. Falha, demora ou conteúdo ilegível contam como "nenhuma lembrança". Nunca lança. */
  load: () => Promise<void>;
  /** Guarda a resposta que acabou de chegar do servidor (o que não for uma resposta é ignorado). Nunca lança. */
  remember: (serverData: unknown) => Promise<void>;
  resetForTests: () => void;
}

const INITIAL: UpdateMemory = { loaded: false, answer: null };

async function readStored(): Promise<RememberedAnswer | null> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    const text = await Promise.race([
      SecureStore.getItemAsync(REMEMBERED_ANSWER_KEY),
      new Promise<null>((resolve) => {
        timer = setTimeout(() => resolve(null), MEMORY_LOAD_TIMEOUT_MS);
      }),
    ]);
    return parseRememberedAnswer(text);
  } catch {
    return null;
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

export const useRememberedAnswer = create<UpdateMemory & MemoryActions>((set, get) => ({
  ...INITIAL,

  load: async () => {
    if (get().loaded) return;
    const stored = await readStored();
    // Uma resposta do servidor que chegou enquanto a leitura corria é mais nova do que o que estava gravado.
    if (get().loaded) return;
    set({ loaded: true, answer: stored });
  },

  remember: async (serverData) => {
    const text = serializeAnswer(serverData);
    if (text === null) return;
    const current = get().answer;
    if (get().loaded && current !== null && JSON.stringify(current) === text) return;
    set({ loaded: true, answer: parseRememberedAnswer(text) });
    try {
      await SecureStore.setItemAsync(REMEMBERED_ANSWER_KEY, text);
    } catch {
      // Não gravou: a resposta vale enquanto o app estiver aberto; na próxima abertura ele abre normal e pergunta.
    }
  },

  resetForTests: () => set({ ...INITIAL }),
}));
