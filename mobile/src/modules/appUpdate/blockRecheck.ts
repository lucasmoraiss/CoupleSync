// Com a tela de bloqueio aberta, pergunta de novo ao servidor quando o app volta ao primeiro plano e a
// intervalos. Se a versão mínima foi corrigida no servidor, a resposta nova destrava o aparelho sozinha (o
// layout troca o bloqueio pelas abas), sem a pessoa ter de fechar o app. Sem React aqui: testado em
// __tests__/blockRecheck.test.ts.

/** O servidor responde da memória; um pedido por minuto de um aparelho bloqueado não pesa. */
export const BLOCK_RECHECK_INTERVAL_MS = 60_000;

export interface BlockRecheckDeps {
  readonly recheck: () => void;
  /** `AppState.addEventListener('change', ...)` do React Native (existe em todo APK já instalado). */
  readonly onAppStateChange: (listener: (state: string) => void) => { remove: () => void };
}

/** Começa a reconsultar; devolve a função que para (chamada ao sair da tela). */
export function startBlockRecheck(deps: BlockRecheckDeps): () => void {
  const recheck = () => {
    try {
      deps.recheck();
    } catch {
      // Uma reconsulta que falha não pode derrubar a tela que oferece o download e a saída.
    }
  };
  const subscription = deps.onAppStateChange((state) => {
    if (state === 'active') recheck();
  });
  const timer = setInterval(recheck, BLOCK_RECHECK_INTERVAL_MS);
  return () => {
    clearInterval(timer);
    subscription.remove();
  };
}
