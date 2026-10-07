// Open Finance (Meu Pluggy): regras do wizard de conexão e da tela de gestão. Lógica pura (sem React Native),
// coberta por __tests__/wizard.test.ts.
//
// O progresso guardado no aparelho tem SÓ o passo e campos não sensíveis. O Client ID, o Client Secret e os
// Item IDs digitados ficam apenas na memória da tela e são descartados quando ela perde o foco (resetOnFocus).
import { getApiErrorCode, getApiErrorStatus } from '@/services/apiError';
import type { BankAccountResponse, BankConnectionResponse, OpenFinanceStatusResponse } from '@/types/api';

export const MEU_PLUGGY_URL = 'https://meu.pluggy.ai';
export const DASHBOARD_URL = 'https://dashboard.pluggy.ai';

export const UNAVAILABLE_TITLE = 'Indisponível neste servidor';
export const UNAVAILABLE_TEXT =
  'A conexão com bancos pelo Open Finance depende de uma configuração do servidor do CoupleSync que ainda não foi feita. Quando ela estiver pronta, esta tela passa a mostrar o passo a passo para conectar.';

export const WIZARD_DONE_MESSAGE = 'Conexão pronta. A sincronização chega na próxima atualização do app.';

export const DEFAULT_CONNECTION_LABEL = 'Meus bancos';
export const MAX_LABEL_LENGTH = 60;

/** Passo 2: o que fazer em meu.pluggy.ai. */
export const MEU_PLUGGY_STEPS: readonly string[] = [
  'Crie a sua conta em meu.pluggy.ai (é gratuita).',
  'Toque em "Conectar conta".',
  'Escolha o banco que você quer conectar.',
  'Autorize no app do banco. Repita para cada banco.',
];

/** Passo 3: o que fazer em dashboard.pluggy.ai. */
export const DASHBOARD_STEPS: readonly string[] = [
  'Crie uma conta em dashboard.pluggy.ai com o mesmo e-mail do Meu Pluggy.',
  'Crie uma aplicação (qualquer nome serve).',
  'Abra a aba "Aplicação".',
  'Copie o Client ID e o Client Secret e cole nos campos abaixo.',
];

/** Passo 4: como obter um Item ID por banco. */
export const ITEM_STEPS: readonly string[] = [
  'Na sua aplicação, abra "Conectores" e ligue o conector "MeuPluggy".',
  'Toque em "Ir para Demo" e depois em "Conectar conta".',
  'Escolha "MeuPluggy" e entre com a conta do Meu Pluggy.',
  'Na conexão criada, abra o menu de três pontos e toque em "Copiar Item ID".',
  'Cole o Item ID abaixo. Cada banco tem o seu Item ID.',
];

export type WizardStep = 1 | 2 | 3 | 4;

export const WIZARD_STEP_TITLES: Readonly<Record<WizardStep, string>> = {
  1: 'O que é',
  2: 'Meu Pluggy',
  3: 'Dashboard',
  4: 'Item ID',
};

// ---------------------------------------------------------------- progresso guardado

export const PROGRESS_VERSION = 1;

export interface WizardProgress {
  readonly version: number;
  /** Grupo em que o wizard foi começado: em outro grupo o progresso não vale. */
  readonly coupleId: string | null;
  readonly step: WizardStep;
  /** Caixa "Conectei meus bancos" do passo 2. */
  readonly banksConnected: boolean;
  /** Conexão criada no passo 3 (id do servidor; não é segredo). */
  readonly connectionId: string | null;
}

export const EMPTY_PROGRESS: WizardProgress = {
  version: PROGRESS_VERSION,
  coupleId: null,
  step: 1,
  banksConnected: false,
  connectionId: null,
};

const KEY_PREFIX = 'couplesync_openfinance_wizard_';

/** Chaves do armazenamento seguro aceitam só letras, números, ponto, hífen e sublinhado. */
export function progressStorageKey(userId: string): string {
  return `${KEY_PREFIX}${userId.replace(/[^A-Za-z0-9._-]/g, '_')}`;
}

function isStep(value: unknown): value is WizardStep {
  return value === 1 || value === 2 || value === 3 || value === 4;
}

/** Só os campos conhecidos vão para o armazenamento: nada do que a tela tiver a mais (credenciais) é gravado. */
export function serializeProgress(progress: WizardProgress): string {
  return JSON.stringify({
    version: PROGRESS_VERSION,
    coupleId: progress.coupleId,
    step: progress.step,
    banksConnected: progress.banksConnected === true,
    connectionId: progress.connectionId,
  });
}

/** Lê o progresso guardado; qualquer coisa estranha, de outra versão ou de outro grupo vira "nunca começou". */
export function parseProgress(raw: string | null | undefined, coupleId: string | null): WizardProgress {
  const fresh: WizardProgress = { ...EMPTY_PROGRESS, coupleId };
  if (!raw) return fresh;
  try {
    const data = JSON.parse(raw);
    if (!data || typeof data !== 'object' || Array.isArray(data)) return fresh;
    if (data.version !== PROGRESS_VERSION || !isStep(data.step)) return fresh;
    if (typeof data.coupleId !== 'string' || data.coupleId !== coupleId) return fresh;
    return {
      version: PROGRESS_VERSION,
      coupleId,
      step: data.step,
      banksConnected: data.banksConnected === true,
      connectionId: typeof data.connectionId === 'string' && data.connectionId ? data.connectionId : null,
    };
  } catch {
    return fresh;
  }
}

// ---------------------------------------------------------------- o que habilita cada botão

export function canContinueFromBanks(banksConnected: boolean): boolean {
  return banksConnected;
}

export function canTestCredentials(clientId: string, clientSecret: string, busy: boolean): boolean {
  return !busy && clientId.trim() !== '' && clientSecret.trim() !== '';
}

export interface TestedCredentials {
  readonly clientId: string;
  readonly clientSecret: string;
}

/** As credenciais na tela são exatamente as que passaram no teste? Mudou um caractere, testa de novo. */
export function sameCredentials(tested: TestedCredentials | null, clientId: string, clientSecret: string): boolean {
  return tested !== null && tested.clientId === clientId.trim() && tested.clientSecret === clientSecret.trim();
}

export function canCreateConnection(
  label: string,
  clientId: string,
  clientSecret: string,
  tested: TestedCredentials | null,
  busy: boolean,
): boolean {
  return !busy && label.trim() !== '' && sameCredentials(tested, clientId, clientSecret);
}

/** Passo 4: "Verificar" só com o Item ID preenchido (e sem outra verificação em andamento). */
export function canVerifyItem(itemId: string, busy: boolean): boolean {
  return !busy && itemId.trim() !== '';
}

export function canFinishWizard(items: ReadonlyArray<{ readonly verified: boolean }>): boolean {
  return items.some((item) => item.verified);
}

// ---------------------------------------------------------------- status do servidor

/** Sem resposta, com resposta de servidor antigo ou com `available: false`: indisponível. */
export function isOpenFinanceAvailable(status: OpenFinanceStatusResponse | null | undefined): boolean {
  return status?.available === true;
}

/**
 * Servidor que ainda não tem a rota do Open Finance (404 genérico): para o usuário é o mesmo que indisponível.
 * Qualquer outro erro devolve null e segue como erro.
 */
export function statusWhenRouteMissing(error: unknown): OpenFinanceStatusResponse | null {
  return getApiErrorStatus(error) === 404 && getApiErrorCode(error) === 'NOT_FOUND'
    ? { available: false, connections: [] }
    : null;
}

/** Aviso para um banco que o Pluggy diz precisar de atenção; null quando está tudo certo. */
export function itemStatusNote(status: string): string | null {
  if (status === 'LOGIN_ERROR' || status === 'WAITING_USER_INPUT') {
    return 'Este banco precisa de uma ação sua no Meu Pluggy (entrar de novo ou autorizar).';
  }
  if (status === 'OUTDATED') return 'Os dados deste banco estão desatualizados no Meu Pluggy.';
  return null;
}

export function myConnectionOf(status: OpenFinanceStatusResponse | null | undefined): BankConnectionResponse | null {
  return status?.connections?.find((connection) => connection.isMine) ?? null;
}

/** Os controles de edição que a tela de gestão mostra numa conexão. */
export interface ConnectionControls {
  /** Interruptor "sincronizar" de cada conta (sem ele, a conta mostra só "Sincroniza" / "Não sincroniza"). */
  readonly syncSwitch: boolean;
  readonly addBank: boolean;
  /** "Conectar de novo", no lugar de "Adicionar banco", para quem desconectou. */
  readonly reconnect: boolean;
  readonly disconnect: boolean;
}

/**
 * Todos do grupo veem bancos, contas e saldos; os controles de edição são só de quem conectou. Os botões
 * dependem também de o servidor estar disponível (`available`).
 */
export function connectionControls(
  connection: Pick<BankConnectionResponse, 'isMine' | 'status'>,
  available: boolean,
): ConnectionControls {
  const mine = connection.isMine === true;
  const disconnected = connection.status === 'Disconnected';
  return {
    syncSwitch: mine,
    addBank: mine && available && !disconnected,
    reconnect: mine && available && disconnected,
    disconnect: mine && available && !disconnected,
  };
}

/**
 * Passo 3, quando guardar a conexão falha. Se o servidor diz que ela já existe (criada em outro aparelho, ou a
 * resposta anterior se perdeu), o wizard segue para o passo 4 com a conexão que o servidor tem: devolve o id
 * dela. Com outro erro (o status nem é buscado), ou se o servidor não mostra uma conexão minha, devolve null e
 * o erro aparece na tela.
 */
export async function existingConnectionAfterCreateError(
  errorCode: string | undefined,
  loadStatus: () => Promise<OpenFinanceStatusResponse | null | undefined>,
): Promise<string | null> {
  if (errorCode !== 'BANK_CONNECTION_ALREADY_EXISTS') return null;
  return myConnectionOf(await loadStatus())?.id ?? null;
}

export function connectionStatusLabel(status: string): string {
  if (status === 'Error') return 'Com erro';
  if (status === 'Disconnected') return 'Desconectada';
  return 'Conectada';
}

/**
 * O que fazer com uma conexão "Com erro"; null nos outros status. Tentar de novo não resolve (o servidor usaria
 * as mesmas credenciais guardadas): o caminho é desconectar e conectar de novo, e só quem conectou pode.
 */
export function connectionErrorHelp(connection: Pick<BankConnectionResponse, 'status' | 'isMine' | 'userName'>): string | null {
  if (connection.status !== 'Error') return null;
  return connection.isMine
    ? 'Para voltar a funcionar, toque em "Desconectar" e depois em "Conectar de novo", com o Client ID e o Client Secret certos. Os bancos e as contas continuam aqui.'
    : `Só ${connection.userName} pode resolver: desconectando e conectando de novo com as credenciais certas.`;
}

/**
 * Em que passo o wizard abre. Quem já tem conexão (ativa ou com erro) só adiciona bancos: passo 4. Quem
 * desconectou precisa informar as credenciais de novo. Sem o aceite do aviso de privacidade, sempre o passo 1.
 */
export function startingStep(input: {
  readonly progress: WizardProgress;
  readonly consentAccepted: boolean;
  readonly myConnection: BankConnectionResponse | null;
}): WizardStep {
  const { progress, consentAccepted, myConnection } = input;
  if (myConnection && myConnection.status !== 'Disconnected') return 4;
  if (!consentAccepted) return 1;
  const resumed: WizardStep = progress.step === 1 ? 2 : progress.step;
  return resumed === 4 ? 3 : resumed;
}

// ---------------------------------------------------------------- contas encontradas

const SUBTYPE_LABELS: Readonly<Record<string, string>> = {
  CHECKING_ACCOUNT: 'conta corrente',
  SAVINGS_ACCOUNT: 'poupança',
  CREDIT_CARD: 'cartão',
};

/** "conta corrente ····1234", "cartão ····5678". */
export function describeAccount(account: Pick<BankAccountResponse, 'type' | 'subtype' | 'numberMasked'>): string {
  const kind = (account.subtype && SUBTYPE_LABELS[account.subtype]) || (account.type === 'CREDIT' ? 'cartão' : 'conta');
  return account.numberMasked ? `${kind} ····${account.numberMasked}` : kind;
}

/** "Banco Exemplo · conta corrente ····1234 · cartão ····5678". */
export function describeItemAccounts(
  connectorName: string,
  accounts: ReadonlyArray<Pick<BankAccountResponse, 'type' | 'subtype' | 'numberMasked'>>,
): string {
  return [connectorName.trim(), ...accounts.map(describeAccount)].filter((part) => part !== '').join(' · ');
}
