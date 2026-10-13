// O último status da análise com IA (GET /api/v1/ai/status) da pessoa logada no grupo ativo.
//
// - Pertence a (usuário, grupo): quem lê confere o dono contra a sessão atual, então depois de trocar de grupo ou
//   de conta o valor antigo não vale para ninguém, mesmo antes de a nova consulta voltar.
// - Fica também guardado no aparelho, numa chave por pessoa (com o grupo a que pertence): na abertura seguinte o
//   botão do Assistente aparece na hora, pelo último valor, enquanto o servidor não responde. O valor guardado
//   nunca passa por cima de uma resposta do servidor.
// - A consulta que falha (API acordando, sem rede) é tentada de novo sozinha, algumas vezes, com espera crescente;
//   pedir de novo (foco do Painel, puxar para atualizar, botão do aviso) consulta na hora.
// - Sair da conta apaga tudo, da memória e do aparelho (limpador registrado em userData.ts).
// - Toda consulta, nova tentativa e escrita fica presa à época da sessão: resposta que chega depois de sair, de
//   outro login ou da troca de grupo é descartada. A resposta de uma consulta que começou antes de uma escrita
//   (ligar, desligar) e chega depois dela também: vale o que a escrita devolveu.
// - A IA vem ligada para todo grupo: aqui não há pergunta nem tela que abra sozinha. Desligar para o grupo e ligar
//   de novo ficam no servidor; o aparelho só guarda a cópia do último status.
import { create } from 'zustand';
import * as SecureStore from 'expo-secure-store';
import { aiApiClient } from '@/services/apiClient';
import { getSessionEpoch, useSessionStore } from '@/state/sessionStore';
import { registerUserDataCleaner } from '@/state/userData';
import type { AiStatusResponse } from '@/types/api';
import { AI_CONSENT_VERSION, AI_STATUS_RETRY_DELAYS_MS, parseStoredAiStatus, serializeStoredAiStatus } from './aiStatus';

interface AiStatusState {
  ownerUserId: string | null;
  ownerCoupleId: string | null;
  status: AiStatusResponse | null;
  /** A última consulta deste dono falhou (sem rede, servidor fora). O valor anterior, se havia, continua. */
  loadFailed: boolean;
  /** `status` veio do que estava guardado no aparelho; o servidor ainda não respondeu nesta abertura. */
  fromStorage: boolean;
  /** A consulta falhou e outra tentativa está marcada. */
  retrying: boolean;
  /** Quantas respostas do servidor já chegaram (consulta ou escrita). As telas reagem a cada uma. */
  freshCount: number;
}

interface AiStatusActions {
  /** Consulta o servidor agora. Devolve o status novo, ou null se falhou ou se a sessão mudou no meio. */
  refresh: () => Promise<AiStatusResponse | null>;
  /**
   * Liga de novo a IA do grupo que a desligou: POST /ai/consent com a versão do texto, a rota que já existia (na
   * API anterior ela também liga, gravando o aceite). Erros sobem para a tela mostrar.
   */
  turnOn: () => Promise<AiStatusResponse | null>;
  /** Desliga a IA para o grupo inteiro (DELETE /ai/consent?scope=group). Erros sobem para a tela mostrar. */
  turnOffForGroup: () => Promise<AiStatusResponse | null>;
  /** Esquece tudo: a memória, as tentativas marcadas e a cópia guardada no aparelho. */
  reset: () => Promise<void>;
}

const INITIAL: AiStatusState = {
  ownerUserId: null,
  ownerCoupleId: null,
  status: null,
  loadFailed: false,
  fromStorage: false,
  retrying: false,
  freshCount: 0,
};

const STORAGE_KEY_PREFIX = 'couplesync_ai_status_';

/** Chaves do armazenamento seguro aceitam só letras, números, ponto, hífen e sublinhado. */
export function aiStatusStorageKey(userId: string): string {
  return `${STORAGE_KEY_PREFIX}${userId.replace(/[^A-Za-z0-9._-]/g, '_')}`;
}

interface Owner {
  userId: string;
  coupleId: string;
}

function sessionOwner(): Owner | null {
  const { userId, coupleId, accessToken } = useSessionStore.getState();
  return accessToken && userId && coupleId ? { userId, coupleId } : null;
}

/**
 * A última pessoa que teve sessão neste aparelho desde que o app abriu. Sair da conta zera a sessão ANTES de rodar
 * os limpadores (userData.ts); é por aqui que o limpador sabe de quem é a cópia a apagar quando o store não chegou
 * a ter dono (ninguém consultou o status: a pessoa abriu o app sem grupo).
 */
let lastSessionUserId: string | null = useSessionStore.getState().userId;
useSessionStore.subscribe((session) => {
  if (session.userId !== null) lastSessionUserId = session.userId;
});

export const useAiStatusStore = create<AiStatusState & AiStatusActions>((set, get) => {
  let retryTimer: ReturnType<typeof setTimeout> | undefined;
  /** A consulta em andamento e a época em que começou: pedidos simultâneos viram uma chamada só. */
  let reading: { epoch: number; promise: Promise<AiStatusResponse | null> } | null = null;
  /**
   * Quantas escritas (ligar, desligar) já tiveram a resposta guardada. Uma consulta que começou antes
   * de uma delas traz o que valia antes: a resposta dela, ou a falha, não passa por cima do resultado da escrita.
   */
  let writesDone = 0;

  function cancelRetry(): void {
    if (retryTimer !== undefined) clearTimeout(retryTimer);
    retryTimer = undefined;
  }

  function owns(owner: Owner): boolean {
    const state = get();
    return state.ownerUserId === owner.userId && state.ownerCoupleId === owner.coupleId;
  }

  /** Lê a cópia do aparelho. Só vale se ninguém trocou de sessão no meio e o servidor ainda não respondeu. */
  async function loadStored(owner: Owner, epoch: number): Promise<void> {
    let raw: string | null = null;
    try {
      raw = await SecureStore.getItemAsync(aiStatusStorageKey(owner.userId));
    } catch {
      return; // Aparelho que não lê o armazenamento: fica só com o que o servidor responder.
    }
    if (getSessionEpoch() !== epoch || !owns(owner) || get().status !== null) return;
    const stored = parseStoredAiStatus(raw, owner.coupleId);
    if (stored) set({ status: stored, fromStorage: true });
  }

  /** Guarda a cópia no aparelho. Se a pessoa saiu da conta enquanto gravava, a cópia é apagada de novo. */
  async function store(owner: Owner, status: AiStatusResponse): Promise<void> {
    const key = aiStatusStorageKey(owner.userId);
    try {
      await SecureStore.setItemAsync(key, serializeStoredAiStatus(owner.coupleId, status));
      if (useSessionStore.getState().userId !== owner.userId) await SecureStore.deleteItemAsync(key);
    } catch {
      // Não gravou: a próxima abertura só não terá o valor na hora.
    }
  }

  /** Passa a guardar para o dono da sessão atual; o que era de outro dono é esquecido. */
  function claim(owner: Owner, epoch: number): void {
    if (owns(owner)) return;
    cancelRetry();
    set({ ...INITIAL, ownerUserId: owner.userId, ownerCoupleId: owner.coupleId });
    void loadStored(owner, epoch);
  }

  /** Uma resposta do servidor: passa a valer, encerra as tentativas marcadas e vai para a cópia do aparelho. */
  function accept(owner: Owner, status: AiStatusResponse): void {
    cancelRetry();
    set({ status, loadFailed: false, fromStorage: false, retrying: false, freshCount: get().freshCount + 1 });
    void store(owner, status);
  }

  async function read(owner: Owner, epoch: number, attempt: number): Promise<AiStatusResponse | null> {
    const writesBefore = writesDone;
    try {
      const { data } = await aiApiClient.getStatus();
      if (getSessionEpoch() !== epoch) return null;
      // Uma escrita foi respondida enquanto esta consulta estava no ar: vale o que a escrita devolveu.
      if (writesDone !== writesBefore) return owns(owner) ? get().status : null;
      accept(owner, data);
      return data;
    } catch {
      if (getSessionEpoch() !== epoch) return null;
      if (writesDone !== writesBefore) return owns(owner) ? get().status : null;
      const delay = AI_STATUS_RETRY_DELAYS_MS[attempt];
      set({ loadFailed: true, retrying: delay !== undefined });
      if (delay !== undefined) {
        cancelRetry();
        retryTimer = setTimeout(() => {
          retryTimer = undefined;
          if (getSessionEpoch() === epoch) void startRead(attempt + 1);
        }, delay);
      }
      return null;
    }
  }

  function startRead(attempt: number): Promise<AiStatusResponse | null> {
    const owner = sessionOwner();
    if (!owner) return Promise.resolve(null);
    const epoch = getSessionEpoch();
    if (reading && reading.epoch === epoch) return reading.promise;
    claim(owner, epoch);
    const promise = read(owner, epoch, attempt).finally(() => {
      if (reading?.promise === promise) reading = null;
    });
    reading = { epoch, promise };
    return promise;
  }

  /** Roda uma escrita no servidor e guarda o status que ela devolve, se a sessão ainda for a mesma. */
  async function write(call: () => Promise<{ data: AiStatusResponse }>): Promise<AiStatusResponse | null> {
    const owner = sessionOwner();
    if (!owner) return null;
    const epoch = getSessionEpoch();
    claim(owner, epoch);
    const { data } = await call();
    if (getSessionEpoch() !== epoch) return null;
    writesDone += 1;
    // A consulta que ainda estiver no ar é de antes desta escrita: a próxima não se junta a ela.
    reading = null;
    accept(owner, data);
    return data;
  }

  return {
    ...INITIAL,
    refresh: () => {
      // Pedido explícito (foco, puxar para atualizar, botão): consulta agora, sem esperar a tentativa marcada.
      cancelRetry();
      // Quem tocou em "Verificar agora" vê que a consulta recomeçou, em vez do aviso de falha parado.
      const owner = sessionOwner();
      if (owner && owns(owner) && get().loadFailed) set({ loadFailed: false, retrying: false });
      return startRead(0);
    },
    turnOn: () => write(() => aiApiClient.accept(AI_CONSENT_VERSION)),
    turnOffForGroup: () => write(() => aiApiClient.revoke('group')),
    reset: async () => {
      const previousOwner = get().ownerUserId;
      cancelRetry();
      set({ ...INITIAL });
      const leaving = new Set<string>();
      if (previousOwner !== null) leaving.add(previousOwner);
      // Quem saiu sem o store ter tido dono nesta abertura do app (abriu já sem grupo): a cópia que ficou de uma
      // abertura anterior também é apagada. Quem está entrando agora (limpeza do login) fica com a sua.
      const current = useSessionStore.getState().userId;
      if (lastSessionUserId !== null && lastSessionUserId !== current) leaving.add(lastSessionUserId);
      lastSessionUserId = current;
      for (const userId of leaving) {
        try {
          await SecureStore.deleteItemAsync(aiStatusStorageKey(userId));
        } catch {
          // Não apagou: a cópia é de uma chave só desta pessoa, e só vale para o grupo que está escrito nela.
        }
      }
    },
  };
});

/** O que vale para a sessão atual: o estado guardado só conta se for do usuário e do grupo de agora. */
export function selectForSession(
  state: Pick<AiStatusState, 'ownerUserId' | 'ownerCoupleId' | 'status' | 'loadFailed'>,
  userId: string | null,
  coupleId: string | null,
): { status: AiStatusResponse | null; loadFailed: boolean } {
  const mine = userId !== null && coupleId !== null && state.ownerUserId === userId && state.ownerCoupleId === coupleId;
  return mine ? { status: state.status, loadFailed: state.loadFailed } : { status: null, loadFailed: false };
}

/** Como o status da sessão atual chegou: do aparelho (o servidor ainda não respondeu) e se há nova tentativa marcada. */
export function selectLoadForSession(
  state: Pick<AiStatusState, 'ownerUserId' | 'ownerCoupleId' | 'fromStorage' | 'retrying'>,
  userId: string | null,
  coupleId: string | null,
): { fromStorage: boolean; retrying: boolean } {
  const mine = userId !== null && coupleId !== null && state.ownerUserId === userId && state.ownerCoupleId === coupleId;
  return mine ? { fromStorage: state.fromStorage, retrying: state.retrying } : { fromStorage: false, retrying: false };
}

/** Fora do React (ex.: ao montar o envio de um extrato): o status da sessão atual, ou null. */
export function currentAiStatus(): AiStatusResponse | null {
  const { userId, coupleId } = useSessionStore.getState();
  return selectForSession(useAiStatusStore.getState(), userId, coupleId).status;
}

registerUserDataCleaner(() => useAiStatusStore.getState().reset());
