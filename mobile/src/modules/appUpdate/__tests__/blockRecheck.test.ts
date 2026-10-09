// Issue #3 (B7): a tela de bloqueio pergunta de novo ao servidor quando o app volta ao primeiro plano e a
// intervalos enquanto está na tela. Corrigida a versão mínima no servidor, o aparelho destrava sozinho.
import { BLOCK_RECHECK_INTERVAL_MS, startBlockRecheck } from '../blockRecheck';

function harness() {
  const recheck = jest.fn();
  let appStateListener: ((state: string) => void) | null = null;
  const removeAppStateListener = jest.fn(() => {
    appStateListener = null;
  });
  const stop = startBlockRecheck({
    recheck,
    onAppStateChange: (listener) => {
      appStateListener = listener;
      return { remove: removeAppStateListener };
    },
  });
  return {
    recheck,
    stop,
    removeAppStateListener,
    appState: (state: string) => appStateListener?.(state),
    hasListener: () => appStateListener !== null,
  };
}

beforeEach(() => jest.useFakeTimers());
afterEach(() => jest.useRealTimers());

describe('reconsulta com o bloqueio na tela', () => {
  it('pergunta de novo quando o app volta ao primeiro plano (e só nesse caso)', () => {
    const h = harness();

    h.appState('background');
    h.appState('inactive');
    expect(h.recheck).not.toHaveBeenCalled();

    h.appState('active');
    expect(h.recheck).toHaveBeenCalledTimes(1);
  });

  it('pergunta de novo a intervalos enquanto a tela está aberta', () => {
    const h = harness();

    jest.advanceTimersByTime(BLOCK_RECHECK_INTERVAL_MS - 1);
    expect(h.recheck).not.toHaveBeenCalled();

    jest.advanceTimersByTime(1);
    expect(h.recheck).toHaveBeenCalledTimes(1);

    jest.advanceTimersByTime(BLOCK_RECHECK_INTERVAL_MS * 3);
    expect(h.recheck).toHaveBeenCalledTimes(4);
  });

  it('o intervalo é de minutos: nem martela o servidor, nem deixa a pessoa esperando muito', () => {
    expect(BLOCK_RECHECK_INTERVAL_MS).toBeGreaterThanOrEqual(30_000);
    expect(BLOCK_RECHECK_INTERVAL_MS).toBeLessThanOrEqual(5 * 60_000);
  });

  it('ao sair da tela (destravou, saiu da conta) para de perguntar', () => {
    const h = harness();

    h.stop();
    jest.advanceTimersByTime(BLOCK_RECHECK_INTERVAL_MS * 5);

    expect(h.recheck).not.toHaveBeenCalled();
    expect(h.removeAppStateListener).toHaveBeenCalledTimes(1);
    expect(h.hasListener()).toBe(false);
  });

  it('uma reconsulta que lança não derruba a tela nem para as seguintes', () => {
    const h = harness();
    h.recheck.mockImplementationOnce(() => {
      throw new Error('falhou');
    });

    expect(() => h.appState('active')).not.toThrow();
    jest.advanceTimersByTime(BLOCK_RECHECK_INTERVAL_MS);

    expect(h.recheck).toHaveBeenCalledTimes(2);
  });
});
