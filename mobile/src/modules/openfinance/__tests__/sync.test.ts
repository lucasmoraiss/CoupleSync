// Open Finance (issue #25): regras puras da sincronização no app (período, progresso, pedido silencioso ao abrir).
// Tudo aqui é inventado; nenhum dado bancário real.
import {
  AUTO_SYNC_AFTER_MS,
  HISTORY_OPTIONS,
  autoSyncOnOpen,
  canSyncNow,
  connectionToAutoSync,
  historyLabel,
  historyMonthsWithCredentials,
  isRunFinished,
  lastSyncText,
  neverSynced,
  requestAndFollowSync,
  runProgressText,
  runResultText,
  shouldClearSyncOnRefocus,
  syncActionOf,
  syncQuery,
  RUN_POLL_MS,
  RUN_WAIT_LIMIT_MS,
  type FollowSyncDeps,
  type SyncRunState,
} from '../sync';
import type { BankConnectionResponse, OpenFinanceStatusResponse, SyncRunResponse } from '@/types/api';

const NOW = Date.parse('2026-10-07T15:00:00Z');

const connection = (over: Partial<BankConnectionResponse> = {}): BankConnectionResponse => ({
  id: 'conn-1',
  label: 'Meus bancos',
  userId: 'user-1',
  userName: 'Ana',
  isMine: true,
  status: 'Active',
  clientIdHint: '0a1b',
  historyMonths: 3,
  lastSyncAtUtc: '2026-10-07T06:00:00Z',
  lastErrorCode: null,
  lastErrorMessage: null,
  createdAtUtc: '2026-10-01T12:00:00Z',
  items: [{ id: 'item-1', connectorName: 'Banco Exemplo', status: 'UPDATED', executionStatus: 'SUCCESS', lastUpdatedAtUtc: null, lastErrorMessage: null, accounts: [] }],
  ...over,
});

const status = (connections: BankConnectionResponse[], available = true): OpenFinanceStatusResponse => ({ available, connections });

const run = (over: Partial<SyncRunResponse> = {}): SyncRunResponse => ({
  id: 'run-1',
  connectionId: 'conn-1',
  status: 'Pending',
  triggeredBy: 'User',
  createdAtUtc: '2026-10-07T15:00:00Z',
  startedAtUtc: null,
  finishedAtUtc: null,
  transactionsNew: 0,
  transactionsUpdated: 0,
  errorCode: null,
  errorMessage: null,
  ...over,
});

describe('período do histórico', () => {
  it('oferece 3, 6 e 12 meses', () => {
    expect(HISTORY_OPTIONS).toEqual([3, 6, 12]);
    expect(HISTORY_OPTIONS.map(historyLabel)).toEqual(['3 meses', '6 meses', '12 meses']);
  });
});

describe('pedido de sincronização', () => {
  it('manda só o que foi pedido', () => {
    expect(syncQuery({})).toBe('');
    expect(syncQuery({ force: true })).toBe('?force=true');
    expect(syncQuery({ appOpen: true })).toBe('?appOpen=true');
    expect(syncQuery({ historyMonths: 12, aiConsent: true })).toBe('?aiCategorizationConsent=true&historyMonths=12');
    expect(syncQuery({ force: true, aiConsent: false, historyMonths: 6 })).toBe('?force=true&historyMonths=6');
  });

  it('o pedido silencioso de abertura nunca força a atualização no banco', () => {
    expect(syncQuery({ appOpen: true, force: true })).toBe('?appOpen=true');
  });
});

describe('acompanhamento da sincronização', () => {
  it('termina em Done ou Failed', () => {
    expect(isRunFinished(run({ status: 'Pending' }))).toBe(false);
    expect(isRunFinished(run({ status: 'Running' }))).toBe(false);
    expect(isRunFinished(run({ status: 'Done' }))).toBe(true);
    expect(isRunFinished(run({ status: 'Failed' }))).toBe(true);
    expect(isRunFinished(null)).toBe(false);
  });

  it('diz o que está acontecendo enquanto espera', () => {
    expect(runProgressText(null)).toBe('Pedindo a sincronização…');
    expect(runProgressText(run({ status: 'Pending' }))).toBe('Na fila para sincronizar…');
    expect(runProgressText(run({ status: 'Running' }))).toBe('Buscando as transações no banco…');
    expect(runProgressText(run({ status: 'Running', transactionsNew: 40, transactionsUpdated: 2 }))).toBe(
      'Buscando as transações no banco… 42 até agora',
    );
  });

  it('o resultado conta as novas; a falha mostra o texto que veio do servidor', () => {
    expect(runResultText(run({ status: 'Done', transactionsNew: 0 }))).toBe('Sincronização concluída. Nenhuma transação nova.');
    expect(runResultText(run({ status: 'Done', transactionsNew: 1 }))).toBe('Sincronização concluída. 1 transação nova.');
    expect(runResultText(run({ status: 'Done', transactionsNew: 57 }))).toBe('Sincronização concluída. 57 transações novas.');
    expect(runResultText(run({ status: 'Failed', errorMessage: 'O Pluggy não respondeu agora. Tente de novo em alguns minutos.' }))).toBe(
      'O Pluggy não respondeu agora. Tente de novo em alguns minutos.',
    );
    expect(runResultText(run({ status: 'Failed', errorMessage: null }))).toBe('Não foi possível sincronizar agora. Tente de novo em alguns minutos.');
  });
});

describe('tela Open Finance', () => {
  it('"Sincronizar agora" é só de quem conectou, com o servidor disponível, a conexão ligada e algum banco', () => {
    expect(canSyncNow(connection(), true, false)).toBe(true);
    expect(canSyncNow(connection({ status: 'Error' }), true, false)).toBe(true);
    expect(canSyncNow(connection({ isMine: false }), true, false)).toBe(false);
    expect(canSyncNow(connection({ status: 'Disconnected' }), true, false)).toBe(false);
    expect(canSyncNow(connection({ items: [] }), true, false)).toBe(false);
    expect(canSyncNow(connection(), false, false)).toBe(false);
    expect(canSyncNow(connection(), true, true)).toBe(false);
  });

  it('mostra a última sincronização no horário de Brasília, ou que ainda não houve', () => {
    expect(lastSyncText(connection({ lastSyncAtUtc: '2026-10-07T01:30:00Z' }))).toBe('Última sincronização: 06/10/2026 às 22:30');
    expect(lastSyncText(connection({ lastSyncAtUtc: null }))).toBe('Ainda sem sincronização.');
    expect(lastSyncText(connection({ lastSyncAtUtc: 'não é data' }))).toBe('Ainda sem sincronização.');
  });
});

describe('primeira sincronização: o período é escolhido antes (revisão 1, I7)', () => {
  it('conexão que nunca sincronizou leva à escolha do período, não a "Sincronizar agora"', () => {
    expect(syncActionOf(connection({ lastSyncAtUtc: null }), true)).toBe('choosePeriod');
    expect(syncActionOf(connection({ lastSyncAtUtc: null, status: 'Error' }), true)).toBe('choosePeriod');
    expect(syncActionOf(connection({ lastSyncAtUtc: 'não é data' }), true)).toBe('choosePeriod');
  });

  it('depois da primeira sincronização o período não aparece mais: só "Sincronizar agora"', () => {
    expect(syncActionOf(connection(), true)).toBe('syncNow');
    expect(syncActionOf(connection({ status: 'Error' }), true)).toBe('syncNow');
  });

  it('quem não conectou, conexão desligada, sem banco ou servidor indisponível: nenhum dos dois', () => {
    for (const lastSyncAtUtc of [null, '2026-10-07T06:00:00Z']) {
      expect(syncActionOf(connection({ lastSyncAtUtc, isMine: false }), true)).toBe('none');
      expect(syncActionOf(connection({ lastSyncAtUtc, status: 'Disconnected' }), true)).toBe('none');
      expect(syncActionOf(connection({ lastSyncAtUtc, items: [] }), true)).toBe('none');
      expect(syncActionOf(connection({ lastSyncAtUtc }), false)).toBe('none');
    }
  });

  it('"nunca sincronizou": sem data, com data inválida ou sem conexão carregada ainda', () => {
    expect(neverSynced(connection({ lastSyncAtUtc: null }))).toBe(true);
    expect(neverSynced(connection({ lastSyncAtUtc: 'ontem' }))).toBe(true);
    expect(neverSynced(null)).toBe(true);
    expect(neverSynced(connection())).toBe(false);
  });

  it('passo 3: o período só vai junto com as credenciais enquanto a conexão nunca sincronizou (revisão 3, I1)', () => {
    // Conexão nova, ou reconexão de uma que nunca sincronizou: o padrão de 3 meses (a escolha é no passo 5).
    expect(historyMonthsWithCredentials(null)).toBe(3);
    expect(historyMonthsWithCredentials(connection({ status: 'Disconnected', lastSyncAtUtc: null, historyMonths: 12 }))).toBe(3);
    // "Desconectar e conectar de novo" numa conexão que já sincronizou: nada é enviado, e o servidor mantém os
    // 6 ou 12 meses escolhidos. Enviar 3 aqui era o que apagava a escolha.
    expect(historyMonthsWithCredentials(connection({ status: 'Disconnected', historyMonths: 12 }))).toBeUndefined();
    expect(historyMonthsWithCredentials(connection({ status: 'Disconnected', historyMonths: 6 }))).toBeUndefined();
  });
});

describe('pedir e acompanhar uma sincronização', () => {
  /** O servidor de mentira: responde ao pedido com `accepted` e, a cada consulta, com o próximo de `polls`. */
  function follow(accepted: SyncRunResponse | Error | object, polls: Array<SyncRunResponse | Error> = []) {
    const states: SyncRunState[] = [];
    const acceptedCalls: Array<{ run: SyncRunResponse; phasesSoFar: string[] }> = [];
    const asked: string[] = [];
    let clock = 0;
    let current = true;
    let next = 0;
    const deps: { -readonly [K in keyof FollowSyncDeps]: FollowSyncDeps[K] } = {
      requestSync: async (connectionId, query) => {
        asked.push(`${connectionId}${query}`);
        if (accepted instanceof Error || !('id' in accepted)) throw accepted;
        return accepted as SyncRunResponse;
      },
      getSyncRun: async () => {
        const answer = polls[Math.min(next++, polls.length - 1)];
        if (answer instanceof Error) throw answer;
        return answer;
      },
      wait: async (ms) => {
        clock += ms;
      },
      now: () => clock,
      isCurrent: () => current,
      onState: (state) => states.push(state),
      onAccepted: (accepted) => acceptedCalls.push({ run: accepted, phasesSoFar: states.map((s) => `${s.phase}:${s.run?.status ?? '-'}`) }),
    };
    return { deps, states, acceptedCalls, asked, leave: () => (current = false), polled: () => next };
  }

  it('avisa que o pedido foi aceito assim que o servidor responde, antes de a sincronização terminar (M1)', async () => {
    const f = follow(run(), [run({ status: 'Running' }), run({ status: 'Done', transactionsNew: 4 })]);

    const result = await requestAndFollowSync('conn-1', { historyMonths: 6 }, f.deps);

    expect(f.asked).toEqual(['conn-1?historyMonths=6']);
    expect(f.acceptedCalls).toHaveLength(1);
    expect(f.acceptedCalls[0].run.status).toBe('Pending');
    // Quando o aceite foi avisado, a tela só tinha mostrado "pedindo": nada da execução ainda.
    expect(f.acceptedCalls[0].phasesSoFar).toEqual(['working:-']);
    expect(result?.status).toBe('Done');
    expect(f.states.map((s) => `${s.phase}:${s.run?.status ?? '-'}`)).toEqual([
      'working:-',
      'working:Pending',
      'working:Running',
      'working:Done',
      'done:Done',
    ]);
  });

  it('quem sai da tela no meio já teve o aceite avisado, e nada mais é mostrado nem consultado', async () => {
    const f = follow(run(), [run({ status: 'Running' })]);
    f.deps.wait = async () => {
      f.leave();
    };

    await expect(requestAndFollowSync('conn-1', {}, f.deps)).resolves.toBeNull();

    expect(f.acceptedCalls).toHaveLength(1);
    expect(f.polled()).toBe(0);
    expect(f.states.map((s) => s.phase)).toEqual(['working', 'working']);
  });

  it('pedido recusado ("já há uma sincronização em andamento"): mostra o texto do servidor e não avisa aceite', async () => {
    const refused = { response: { status: 409, data: { code: 'SYNC_ALREADY_RUNNING', message: 'Já há uma sincronização em andamento para esta conexão. Aguarde ela terminar.' } } };
    const f = follow(refused);

    await expect(requestAndFollowSync('conn-1', { force: true }, f.deps)).resolves.toBeNull();

    expect(f.acceptedCalls).toEqual([]);
    expect(f.states[f.states.length - 1]).toEqual({
      phase: 'failed',
      run: null,
      requestError: 'Já há uma sincronização em andamento para esta conexão. Aguarde ela terminar.',
    });
  });

  it('a sincronização que falha termina em "failed" com a sincronização (o texto vem dela)', async () => {
    const f = follow(run(), [run({ status: 'Failed', errorCode: 'SYNC_TIMED_OUT', errorMessage: 'A sincronização demorou demais e foi encerrada. Sincronize de novo.' })]);

    const result = await requestAndFollowSync('conn-1', {}, f.deps);

    expect(result?.status).toBe('Failed');
    const last = f.states[f.states.length - 1];
    expect(last.phase).toBe('failed');
    expect(runResultText(last.run!)).toBe('A sincronização demorou demais e foi encerrada. Sincronize de novo.');
  });

  it('consulta que falha (rede) não encerra; no limite de espera a tela desiste e a sincronização segue', async () => {
    const f = follow(run(), [new Error('sem rede'), run({ status: 'Running' })]);

    const result = await requestAndFollowSync('conn-1', {}, f.deps);

    expect(result?.status).toBe('Running');
    expect(f.states[f.states.length - 1].phase).toBe('stillRunning');
    expect(f.polled()).toBeGreaterThanOrEqual(Math.floor(RUN_WAIT_LIMIT_MS / RUN_POLL_MS));
  });
});

describe('volta à tela Open Finance (aba oculta que continua montada; revisão 1, I4)', () => {
  it('o resultado de uma sincronização pedida numa visita anterior não fica para a seguinte', () => {
    expect(shouldClearSyncOnRefocus('done')).toBe(true);
    expect(shouldClearSyncOnRefocus('failed')).toBe(true);
    expect(shouldClearSyncOnRefocus('stillRunning')).toBe(true);
  });

  it('uma sincronização ainda em andamento continua sendo acompanhada; sem nada, não há o que limpar', () => {
    expect(shouldClearSyncOnRefocus('working')).toBe(false);
    expect(shouldClearSyncOnRefocus('idle')).toBe(false);
  });
});

describe('sincronização silenciosa ao abrir o app', () => {
  it('escolhe a minha conexão quando a última sincronização tem mais de 6 horas', () => {
    expect(AUTO_SYNC_AFTER_MS).toBe(6 * 60 * 60 * 1000);
    expect(connectionToAutoSync(status([connection({ lastSyncAtUtc: '2026-10-07T08:59:59Z' })]), NOW)?.id).toBe('conn-1');
    expect(connectionToAutoSync(status([connection({ lastSyncAtUtc: '2026-10-07T09:00:01Z' })]), NOW)).toBeNull();
    expect(connectionToAutoSync(status([connection({ lastSyncAtUtc: '2026-10-07T14:59:00Z' })]), NOW)).toBeNull();
  });

  it('não pede para a conexão de outra pessoa, desconectada, sem banco, nunca sincronizada ou com o servidor indisponível', () => {
    const stale = '2026-10-06T00:00:00Z';
    expect(connectionToAutoSync(status([connection({ isMine: false, lastSyncAtUtc: stale })]), NOW)).toBeNull();
    expect(connectionToAutoSync(status([connection({ status: 'Disconnected', lastSyncAtUtc: stale })]), NOW)).toBeNull();
    expect(connectionToAutoSync(status([connection({ items: [], lastSyncAtUtc: stale })]), NOW)).toBeNull();
    // Quem ainda não escolheu o período (nunca sincronizou) decide no wizard ou no botão, não em silêncio.
    expect(connectionToAutoSync(status([connection({ lastSyncAtUtc: null })]), NOW)).toBeNull();
    expect(connectionToAutoSync(status([connection({ lastSyncAtUtc: stale })], false), NOW)).toBeNull();
    expect(connectionToAutoSync(null, NOW)).toBeNull();
    expect(connectionToAutoSync(status([]), NOW)).toBeNull();
  });

  it('entre as conexões do grupo, só a minha é pedida', () => {
    const mine = connection({ id: 'mine', lastSyncAtUtc: '2026-10-06T00:00:00Z' });
    const partner = connection({ id: 'partner', isMine: false, lastSyncAtUtc: '2026-10-01T00:00:00Z' });
    expect(connectionToAutoSync(status([partner, mine]), NOW)?.id).toBe('mine');
  });

  const deps = (over: Partial<Parameters<typeof autoSyncOnOpen>[0]> = {}) => {
    let epoch = 7;
    const requested: Array<{ id: string; query: string }> = [];
    return {
      requested,
      setEpoch: (value: number) => {
        epoch = value;
      },
      deps: {
        getEpoch: () => epoch,
        now: () => NOW,
        loadStatus: async () => status([connection({ lastSyncAtUtc: '2026-10-06T00:00:00Z' })]),
        requestSync: async (id: string, query: string) => {
          requested.push({ id, query });
        },
        aiConsent: () => false,
        ...over,
      },
    };
  };

  it('pede a sincronização marcada como abertura do app, sem forçar', async () => {
    const d = deps();
    await expect(autoSyncOnOpen(d.deps)).resolves.toBe('requested');
    expect(d.requested).toEqual([{ id: 'conn-1', query: '?appOpen=true' }]);
  });

  it('leva o consentimento de IA de quem conectou, quando existe', async () => {
    const d = deps({ aiConsent: () => true });
    await autoSyncOnOpen(d.deps);
    expect(d.requested[0].query).toBe('?appOpen=true&aiCategorizationConsent=true');
  });

  it('não pede nada quando não há o que sincronizar', async () => {
    const d = deps({ loadStatus: async () => status([connection({ lastSyncAtUtc: '2026-10-07T14:00:00Z' })]) });
    await expect(autoSyncOnOpen(d.deps)).resolves.toBe('skipped');
    expect(d.requested).toEqual([]);
  });

  it('erro não aparece: nem ao ler o status, nem ao pedir (409 de "há pouco", sem rede, servidor antigo)', async () => {
    const failingStatus = deps({
      loadStatus: async () => {
        throw new Error('sem rede');
      },
    });
    await expect(autoSyncOnOpen(failingStatus.deps)).resolves.toBe('failed');

    const tooSoon = deps({
      requestSync: async () => {
        throw { response: { status: 409, data: { code: 'SYNC_TOO_SOON', message: 'há pouco' } } };
      },
    });
    await expect(autoSyncOnOpen(tooSoon.deps)).resolves.toBe('failed');
  });

  it('se a sessão mudou enquanto lia o status (saiu, trocou de grupo), não pede nada', async () => {
    const d = deps();
    d.deps.loadStatus = async () => {
      d.setEpoch(8);
      return status([connection({ lastSyncAtUtc: '2026-10-06T00:00:00Z' })]);
    };
    await expect(autoSyncOnOpen(d.deps)).resolves.toBe('skipped');
    expect(d.requested).toEqual([]);
  });
});
