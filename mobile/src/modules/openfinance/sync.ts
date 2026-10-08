// Open Finance (Meu Pluggy): regras da sincronização no app. Lógica pura (sem React Native), coberta por
// __tests__/sync.test.ts.
//
// A sincronização roda no servidor: o app só pede (POST connections/{id}/sync) e acompanha (GET sync-runs/{id}).
// Só quem conectou pede. Ao abrir o app, o pedido é silencioso: nenhum erro dele aparece.
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

// ---------------------------------------------------------------- tela Open Finance

/** O botão "Sincronizar agora": de quem conectou, com o servidor disponível, a conexão ligada e algum banco. */
export function canSyncNow(
  connection: Pick<BankConnectionResponse, 'isMine' | 'status' | 'items'>,
  available: boolean,
  busy: boolean,
): boolean {
  return !busy && available && connection.isMine === true && connection.status !== 'Disconnected' && connection.items.length > 0;
}

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
