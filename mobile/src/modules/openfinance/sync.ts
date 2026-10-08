// Open Finance (Meu Pluggy): regras da sincronização no app. Lógica pura (sem React Native), coberta por
// __tests__/sync.test.ts.
//
// A sincronização roda no servidor: o app só pede (POST connections/{id}/sync) e acompanha (GET sync-runs/{id}).
// Só quem conectou pede. Ao abrir o app, o pedido é silencioso: nenhum erro dele aparece.
import { getApiErrorMessage } from '@/services/apiError';
import { formatBrazilDate, formatBrazilTime } from '@/utils/brazilDateTime';
import type { BankConnectionResponse, OpenFinanceStatusResponse, SyncRunResponse } from '@/types/api';

/** Quanto do passado a primeira sincronização traz (passo 5 do wizard). */
export const HISTORY_OPTIONS = [3, 6, 12] as const;
export type HistoryMonths = (typeof HISTORY_OPTIONS)[number];

export function historyLabel(months: number): string {
  return `${months} meses`;
}

/** Ao abrir o app, pede a sincronização se a última tem mais do que isto. */
export const AUTO_SYNC_AFTER_MS = 6 * 60 * 60 * 1000;

/** De quanto em quanto tempo a tela pergunta pela sincronização que está acompanhando. */
export const RUN_POLL_MS = 2000;
/** Depois disto a tela para de esperar (a sincronização segue no servidor). */
export const RUN_WAIT_LIMIT_MS = 3 * 60 * 1000;

export const SYNC_FAILED_TEXT = 'Não foi possível sincronizar agora. Tente de novo em alguns minutos.';
export const SYNC_STILL_RUNNING_TEXT =
  'A sincronização continua no servidor. Volte daqui a pouco: as transações aparecem na revisão quando ela terminar.';

export interface SyncOptions {
  /** Pede ao Pluggy para ler o banco de novo antes (só o botão "Sincronizar agora"). */
  readonly force?: boolean;
  /** Pedido silencioso da abertura do app. Nunca força. */
  readonly appOpen?: boolean;
  /** Quem conectou aceitou o aviso de IA: só assim uma descrição pode ir para o classificador de categorias. */
  readonly aiConsent?: boolean;
  /** Período escolhido no passo 5 do wizard. */
  readonly historyMonths?: number;
}

/** A query de POST connections/{id}/sync, só com o que foi pedido. */
export function syncQuery(options: SyncOptions): string {
  const parts: string[] = [];
  if (options.appOpen) parts.push('appOpen=true');
  else if (options.force) parts.push('force=true');
  if (options.aiConsent) parts.push('aiCategorizationConsent=true');
  if (options.historyMonths) parts.push(`historyMonths=${options.historyMonths}`);
  return parts.length > 0 ? `?${parts.join('&')}` : '';
}

// ---------------------------------------------------------------- acompanhamento

export function isRunFinished(run: Pick<SyncRunResponse, 'status'> | null | undefined): boolean {
  return run?.status === 'Done' || run?.status === 'Failed';
}

export function runProgressText(run: Pick<SyncRunResponse, 'status' | 'transactionsNew' | 'transactionsUpdated'> | null | undefined): string {
  if (!run) return 'Pedindo a sincronização…';
  if (run.status === 'Pending') return 'Na fila para sincronizar…';
  const read = (run.transactionsNew ?? 0) + (run.transactionsUpdated ?? 0);
  return read > 0 ? `Buscando as transações no banco… ${read} até agora` : 'Buscando as transações no banco…';
}

export function runResultText(run: Pick<SyncRunResponse, 'status' | 'transactionsNew' | 'errorMessage'>): string {
  if (run.status === 'Failed') return run.errorMessage?.trim() || SYNC_FAILED_TEXT;
  const added = run.transactionsNew ?? 0;
  if (added <= 0) return 'Sincronização concluída. Nenhuma transação nova.';
  return added === 1 ? 'Sincronização concluída. 1 transação nova.' : `Sincronização concluída. ${added} transações novas.`;
}

export type SyncPhase = 'idle' | 'working' | 'done' | 'failed' | 'stillRunning';

export interface SyncRunState {
  readonly phase: SyncPhase;
  /** A sincronização acompanhada (null antes da resposta do pedido). */
  readonly run: SyncRunResponse | null;
  /** Por que não deu para pedir (texto da API, em português), quando phase é 'failed' sem run. */
  readonly requestError: string | null;
}

export const SYNC_IDLE: SyncRunState = { phase: 'idle', run: null, requestError: null };

export interface FollowSyncDeps {
  readonly requestSync: (connectionId: string, query: string) => Promise<SyncRunResponse>;
  readonly getSyncRun: (runId: string) => Promise<SyncRunResponse>;
  readonly wait: (ms: number) => Promise<void>;
  readonly now: () => number;
  /** Falso quando a tela saiu, outro pedido começou ou a sessão mudou: nada mais é mostrado nem pedido. */
  readonly isCurrent: () => boolean;
  readonly onState: (state: SyncRunState) => void;
  /** O servidor aceitou o pedido (a sincronização está na fila): chamado uma vez, antes de acompanhar. */
  readonly onAccepted?: (run: SyncRunResponse) => void;
}

/**
 * Pede uma sincronização e acompanha até terminar, ou até o limite de espera (ela segue no servidor). Devolve a
 * sincronização como ficou, ou null quando o pedido foi recusado ou deixou de ser o atual.
 */
export async function requestAndFollowSync(
  connectionId: string,
  options: SyncOptions,
  deps: FollowSyncDeps,
): Promise<SyncRunResponse | null> {
  deps.onState({ phase: 'working', run: null, requestError: null });
  let run: SyncRunResponse;
  try {
    run = await deps.requestSync(connectionId, syncQuery(options));
  } catch (error) {
    if (deps.isCurrent()) deps.onState({ phase: 'failed', run: null, requestError: getApiErrorMessage(error, SYNC_FAILED_TEXT) });
    return null;
  }
  if (!deps.isCurrent()) return null;
  deps.onAccepted?.(run);
  deps.onState({ phase: 'working', run, requestError: null });

  const deadline = deps.now() + RUN_WAIT_LIMIT_MS;
  while (!isRunFinished(run)) {
    if (deps.now() > deadline) {
      if (deps.isCurrent()) deps.onState({ phase: 'stillRunning', run, requestError: null });
      return run;
    }
    await deps.wait(RUN_POLL_MS);
    if (!deps.isCurrent()) return null;
    try {
      run = await deps.getSyncRun(run.id);
    } catch {
      // Uma consulta que falhou (rede) não encerra o acompanhamento: tenta de novo até o limite de espera.
      continue;
    }
    if (!deps.isCurrent()) return null;
    deps.onState({ phase: 'working', run, requestError: null });
  }

  deps.onState({ phase: run.status === 'Done' ? 'done' : 'failed', run, requestError: null });
  return run;
}

/**
 * A tela Open Finance continua montada entre visitas: o resultado de uma sincronização ("concluída…",
 * "sincronizada há pouco…") é de quando foi pedido e some na visita seguinte. Uma sincronização ainda em andamento
 * continua sendo acompanhada.
 */
export function shouldClearSyncOnRefocus(phase: SyncPhase): boolean {
  return phase !== 'idle' && phase !== 'working';
}

// ---------------------------------------------------------------- tela Open Finance

/** O botão "Sincronizar agora": de quem conectou, com o servidor disponível, a conexão ligada e algum banco. */
export function canSyncNow(
  connection: Pick<BankConnectionResponse, 'isMine' | 'status' | 'items'>,
  available: boolean,
  busy: boolean,
): boolean {
  return !busy && available && connection.isMine === true && connection.status !== 'Disconnected' && connection.items.length > 0;
}

/** Nunca sincronizou: quem conectou ainda não escolheu quanto do passado trazer. */
export function neverSynced(connection: Pick<BankConnectionResponse, 'lastSyncAtUtc'> | null | undefined): boolean {
  const at = connection?.lastSyncAtUtc;
  return !at || Number.isNaN(Date.parse(at));
}

/**
 * Passo 3 do wizard: o período que vai junto com as credenciais. Conexão nova, ou reconexão de uma que nunca
 * sincronizou: o padrão de 3 meses (a escolha de verdade é no passo 5). Reconexão de uma conexão que já
 * sincronizou: nada, e o servidor mantém o período escolhido na primeira sincronização, que continua valendo para
 * os bancos adicionados depois.
 */
export function historyMonthsWithCredentials(
  myConnection: Pick<BankConnectionResponse, 'lastSyncAtUtc'> | null | undefined,
): number | undefined {
  return neverSynced(myConnection) ? HISTORY_OPTIONS[0] : undefined;
}

/**
 * O que a tela Open Finance oferece para sincronizar uma conexão. Antes da primeira sincronização a pessoa escolhe
 * o período (passo "Período" do wizard): 'choosePeriod'. Depois da primeira o período não aparece mais: 'syncNow'.
 */
export type SyncAction = 'none' | 'choosePeriod' | 'syncNow';

export function syncActionOf(
  connection: Pick<BankConnectionResponse, 'isMine' | 'status' | 'items' | 'lastSyncAtUtc'>,
  available: boolean,
): SyncAction {
  if (!canSyncNow(connection, available, false)) return 'none';
  return neverSynced(connection) ? 'choosePeriod' : 'syncNow';
}

export const CHOOSE_PERIOD_TEXT = 'Antes da primeira sincronização, escolha quanto do passado trazer.';
export const CHOOSE_PERIOD_BUTTON = 'Escolher período e sincronizar';

export function lastSyncText(connection: Pick<BankConnectionResponse, 'lastSyncAtUtc'>): string {
  const at = connection.lastSyncAtUtc;
  if (!at || Number.isNaN(Date.parse(at))) return 'Ainda sem sincronização.';
  return `Última sincronização: ${formatBrazilDate(at)} às ${formatBrazilTime(at)}`;
}

// ---------------------------------------------------------------- ao abrir o app

/**
 * A conexão a sincronizar em silêncio: a minha, ligada, com banco, cuja última sincronização tem mais de 6 horas.
 * Quem nunca sincronizou ainda não escolheu o período: decide no wizard ou no botão, não aqui.
 */
export function connectionToAutoSync(
  status: OpenFinanceStatusResponse | null | undefined,
  nowMs: number,
): BankConnectionResponse | null {
  if (status?.available !== true) return null;
  const mine = status.connections?.find((connection) => connection.isMine);
  if (!mine || mine.status === 'Disconnected' || mine.items.length === 0 || !mine.lastSyncAtUtc) return null;
  const last = Date.parse(mine.lastSyncAtUtc);
  if (Number.isNaN(last)) return null;
  return nowMs - last > AUTO_SYNC_AFTER_MS ? mine : null;
}

export interface AutoSyncDeps {
  /** Época da sessão (sessionStore): muda ao sair da conta, entrar em outra ou trocar de grupo. */
  readonly getEpoch: () => number;
  readonly now: () => number;
  readonly loadStatus: () => Promise<OpenFinanceStatusResponse | null | undefined>;
  readonly requestSync: (connectionId: string, query: string) => Promise<unknown>;
  readonly aiConsent: () => boolean;
}

/**
 * O pedido silencioso da abertura do app. Nunca lança: sem rede, servidor antigo, "sincronizada há pouco" e
 * qualquer outro erro terminam em 'failed' e nada aparece. Se a sessão mudou enquanto o status era lido, não pede.
 */
export async function autoSyncOnOpen(deps: AutoSyncDeps): Promise<'requested' | 'skipped' | 'failed'> {
  const epoch = deps.getEpoch();
  try {
    const status = await deps.loadStatus();
    if (deps.getEpoch() !== epoch) return 'skipped';
    const connection = connectionToAutoSync(status, deps.now());
    if (!connection) return 'skipped';
    await deps.requestSync(connection.id, syncQuery({ appOpen: true, aiConsent: deps.aiConsent() }));
    return 'requested';
  } catch {
    return 'failed';
  }
}
