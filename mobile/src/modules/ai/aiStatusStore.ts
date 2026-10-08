// O último status da análise com IA (GET /api/v1/ai/status) da pessoa logada no grupo ativo, em memória.
//
// - Pertence a (usuário, grupo): quem lê confere o dono contra a sessão atual, então depois de trocar de grupo ou
//   de conta o valor antigo não vale para ninguém, mesmo antes de a nova consulta voltar.
// - Sair da conta apaga tudo (limpador registrado em userData.ts).
// - Toda consulta e toda escrita fica presa à época da sessão: resposta que chega depois de sair, de outro login
//   ou da troca de grupo é descartada.
// - Nada é gravado no aparelho: ligar, desligar e responder à pergunta ficam no servidor.
import { create } from 'zustand';
import { aiApiClient } from '@/services/apiClient';
import { getSessionEpoch, useSessionStore } from '@/state/sessionStore';
import { registerUserDataCleaner } from '@/state/userData';
import type { AiStatusResponse } from '@/types/api';
import { AI_CONSENT_VERSION, shouldOpenWelcome } from './aiStatus';

interface AiStatusState {
  ownerUserId: string | null;
  ownerCoupleId: string | null;
  status: AiStatusResponse | null;
  /** A última consulta deste dono falhou (sem rede, servidor fora). O valor anterior, se havia, continua. */
  loadFailed: boolean;
  /** A tela de boas-vindas já foi aberta sozinha para este dono nesta abertura do app. */
  welcomeShown: boolean;
}

interface AiStatusActions {
  /** Consulta o servidor. Devolve o status novo, ou null se falhou ou se a sessão mudou no meio. */
  refresh: () => Promise<AiStatusResponse | null>;
  /** Ativa a análise para o grupo (aceite da versão atual do texto). Erros sobem para a tela mostrar. */
  activate: () => Promise<AiStatusResponse | null>;
  /** `group`: desliga para o grupo inteiro. `mine`: retira só o aceite desta pessoa. */
  revoke: (scope: 'mine' | 'group') => Promise<AiStatusResponse | null>;
  /** "Agora não" / "Entendi": a pergunta não volta, neste nem em outro aparelho. */
  answerOnboarding: () => Promise<AiStatusResponse | null>;
  markWelcomeShown: () => void;
  reset: () => void;
}

const INITIAL: AiStatusState = { ownerUserId: null, ownerCoupleId: null, status: null, loadFailed: false, welcomeShown: false };

function sessionOwner(): { userId: string; coupleId: string } | null {
  const { userId, coupleId, accessToken } = useSessionStore.getState();
  return accessToken && userId && coupleId ? { userId, coupleId } : null;
}

export const useAiStatusStore = create<AiStatusState & AiStatusActions>((set, get) => {
  /** Passa a guardar para o dono da sessão atual; o que era de outro dono (status, "já mostrou") é esquecido. */
  function claim(owner: { userId: string; coupleId: string }): void {
    const state = get();
    if (state.ownerUserId === owner.userId && state.ownerCoupleId === owner.coupleId) return;
    set({ ...INITIAL, ownerUserId: owner.userId, ownerCoupleId: owner.coupleId });
  }

  /** Roda uma chamada ao servidor e guarda o status que ela devolve, se a sessão ainda for a mesma. */
  async function run(call: () => Promise<{ data: AiStatusResponse }>, isRead: boolean): Promise<AiStatusResponse | null> {
    const owner = sessionOwner();
    if (!owner) return null;
    const epoch = getSessionEpoch();
    claim(owner);
    try {
      const { data } = await call();
      if (getSessionEpoch() !== epoch) return null;
      set({ status: data, loadFailed: false });
      return data;
    } catch (error) {
      if (!isRead) throw error;
      if (getSessionEpoch() === epoch) set({ loadFailed: true });
      return null;
    }
  }

  return {
    ...INITIAL,
    refresh: () => run(() => aiApiClient.getStatus(), true),
    activate: () => run(() => aiApiClient.accept(AI_CONSENT_VERSION), false),
    revoke: (scope) => run(() => aiApiClient.revoke(scope), false),
    answerOnboarding: () => run(() => aiApiClient.updatePreferences({ onboardingAnswered: true }), false),
    markWelcomeShown: () => {
      const owner = sessionOwner();
      if (!owner) return;
      claim(owner);
      set({ welcomeShown: true });
    },
    reset: () => set({ ...INITIAL }),
  };
});

/** O que vale para a sessão atual: o estado guardado só conta se for do usuário e do grupo de agora. */
export function selectForSession(
  state: Pick<AiStatusState, 'ownerUserId' | 'ownerCoupleId' | 'status' | 'loadFailed' | 'welcomeShown'>,
  userId: string | null,
  coupleId: string | null,
): { status: AiStatusResponse | null; loadFailed: boolean; welcomeShown: boolean } {
  const mine = userId !== null && coupleId !== null && state.ownerUserId === userId && state.ownerCoupleId === coupleId;
  return mine
    ? { status: state.status, loadFailed: state.loadFailed, welcomeShown: state.welcomeShown }
    : { status: null, loadFailed: false, welcomeShown: false };
}

/** A tela de boas-vindas já abriu sozinha para a pessoa e o grupo da sessão atual, nesta abertura do app? */
export function wasWelcomeShown(): boolean {
  const { userId, coupleId } = useSessionStore.getState();
  return selectForSession(useAiStatusStore.getState(), userId, coupleId).welcomeShown;
}

/** Fora do React (ex.: ao montar o envio de um extrato): o status da sessão atual, ou null. */
export function currentAiStatus(): AiStatusResponse | null {
  const { userId, coupleId } = useSessionStore.getState();
  return selectForSession(useAiStatusStore.getState(), userId, coupleId).status;
}

registerUserDataCleaner(() => useAiStatusStore.getState().reset());

export const AI_WELCOME_ROUTE = '/(main)/ai/welcome';
export const HOME_ROUTE = '/';

/**
 * Para onde ir depois de criar ou entrar num grupo: a tela de boas-vindas da IA, se o servidor disser que a
 * pergunta é devida a esta pessoa neste grupo; senão o Painel. Erro, ou demora além de `timeoutMs`, é Painel
 * (que consulta de novo ao abrir): entrar no app nunca fica esperando pela IA.
 */
export async function routeAfterGroupSetup(timeoutMs = 4000): Promise<typeof AI_WELCOME_ROUTE | typeof HOME_ROUTE> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const gaveUp = new Promise<null>((resolve) => {
    timer = setTimeout(() => resolve(null), timeoutMs);
  });
  try {
    const status = await Promise.race([useAiStatusStore.getState().refresh(), gaveUp]);
    if (!shouldOpenWelcome(status, false)) return HOME_ROUTE;
    // Esta abertura conta como "mostrada": o Painel não abre a tela de novo logo em seguida.
    useAiStatusStore.getState().markWelcomeShown();
    return AI_WELCOME_ROUTE;
  } finally {
    if (timer) clearTimeout(timer);
  }
}
