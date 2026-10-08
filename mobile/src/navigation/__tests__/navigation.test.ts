import * as fs from 'fs';
import * as path from 'path';
import { initialVisitState, onScreenBlur, onScreenFocus, visitKey } from '../screenVisit';
import { PARENT_ROUTE, TABS_BACK_BEHAVIOR, parentRouteOf } from '../routes';

const APP_DIR = path.resolve(__dirname, '../../../app');
const read = (relative: string) => fs.readFileSync(path.join(APP_DIR, relative), 'utf8');

describe('visita de tela (M-I1): o estado não sobrevive a uma nova visita', () => {
  it('a primeira vez que a tela recebe foco não a remonta', () => {
    const first = onScreenFocus(initialVisitState);
    expect(first.key).toBe(initialVisitState.key);
    expect(first.visited).toBe(true);
  });

  it('sair da tela troca a chave (senha, código e edição pela metade somem na hora)', () => {
    const focused = onScreenFocus(initialVisitState);
    expect(onScreenBlur(focused).key).not.toBe(focused.key);
  });

  it('voltar à tela troca a chave de novo: a visita começa com os dados de agora', () => {
    const left = onScreenBlur(onScreenFocus(initialVisitState));
    const back = onScreenFocus(left);
    expect(back.key).not.toBe(left.key);
  });

  it('cada visita tem uma chave que nunca se repetiu', () => {
    let state = initialVisitState;
    const keys = new Set<string>();
    for (let visit = 0; visit < 5; visit++) {
      state = onScreenFocus(state);
      expect(keys.has(visitKey(state))).toBe(false);
      keys.add(visitKey(state));
      state = onScreenBlur(state);
    }
  });

  it('a chave também muda com os parâmetros de rota, mesmo dentro da mesma visita', () => {
    const visit = onScreenFocus(initialVisitState);
    expect(visitKey(visit, ['tx-a'])).not.toBe(visitKey(visit, ['tx-b']));
    expect(visitKey(visit, ['tx-a', '10'])).not.toBe(visitKey(visit, ['tx-a', '20']));
    expect(visitKey(visit, ['tx-a'])).toBe(visitKey(visit, ['tx-a']));
    // Parâmetros com separadores não colidem.
    expect(visitKey(visit, ['a,b', 'c'])).not.toBe(visitKey(visit, ['a', 'b,c']));
    expect(visitKey(visit, [undefined])).toBe(visitKey(visit, [null]));
  });
});

describe('voltar das abas ocultas (M-I2)', () => {
  const layout = read('(main)/_layout.tsx');
  const hiddenScreens = [...layout.matchAll(/<Tabs\.Screen name="([^"]+)" options=\{\{ href: null \}\} \/>/g)].map((m) => m[1]);

  it('o layout registra abas ocultas (a leitura do arquivo funcionou)', () => {
    expect(hiddenScreens.length).toBeGreaterThanOrEqual(10);
  });

  it('toda aba oculta tem uma tela-mãe definida, e só elas', () => {
    expect([...hiddenScreens].sort()).toEqual(Object.keys(PARENT_ROUTE).sort());
  });

  it('telas de configuração voltam para Configurações; edição e importação voltam para Transações', () => {
    expect(parentRouteOf('settings/group')).toBe('/(main)/settings');
    expect(parentRouteOf('settings/change-password')).toBe('/(main)/settings');
    expect(parentRouteOf('settings/privacy')).toBe('/(main)/settings');
    expect(parentRouteOf('settings/alerts')).toBe('/(main)/settings');
    expect(parentRouteOf('settings/verify-email')).toBe('/(main)/settings');
    expect(parentRouteOf('settings/capture-consent')).toBe('/(main)/settings');
    expect(parentRouteOf('transactions/edit')).toBe('/(main)/transactions');
  });

  it('Open Finance: a tela de gestão volta para Configurações e o wizard volta para a tela de gestão', () => {
    expect(parentRouteOf('settings/openfinance/index')).toBe('/(main)/settings');
    expect(parentRouteOf('settings/openfinance/wizard')).toBe('/(main)/settings/openfinance');
  });

  it('Open Finance: Configurações tem o item que abre a tela, e a tela busca de novo a cada volta', () => {
    const settings = read('(main)/settings/index.tsx');
    expect(settings).toContain('Open Finance (conectar banco)');
    expect(settings).toMatch(/router\.push\('\/\(main\)\/settings\/openfinance'/);
    const screen = read('(main)/settings/openfinance/index.tsx');
    expect(screen).toMatch(/useOnRefocus\(/);
    expect(screen).toMatch(/<RefreshControl /);
  });

  it('Open Finance: na tela de gestão, cada controle de edição depende da regra testada (connectionControls)', () => {
    const screen = read('(main)/settings/openfinance/index.tsx');
    expect(screen).toMatch(/const controls = connectionControls\(connection, available\);/);
    // O interruptor, o botão de adicionar banco/conectar de novo e o de desconectar.
    expect(screen).toMatch(/\{controls\.syncSwitch \? \(\s*<View style=\{styles\.switchBox\}>[\s\S]{0,200}<Switch\b/);
    expect(screen).toMatch(/\{controls\.addBank \|\| controls\.reconnect \? \(\s*<View style=\{styles\.actions\}>/);
    expect(screen).toMatch(/\{controls\.disconnect \? \(\s*<TouchableOpacity[\s\S]{0,200}confirmDisconnect\(connection\)/);
    // Nenhum deles aparece em outro lugar da tela, fora dessas condições.
    expect(screen.match(/<Switch\b/g)).toHaveLength(1);
    expect(screen.match(/confirmDisconnect\(connection\)/g)).toHaveLength(1);
    expect(screen.match(/styles\.actions\}/g)).toHaveLength(1);
    // "isMine" só sobra no texto "Conectada por você / por Fulano".
    expect(screen.match(/\.isMine\b/g)).toHaveLength(1);
  });

  it('Open Finance: "Sincronizar agora" só aparece pela regra testada (canSyncNow), força a leitura no banco e mostra a última sincronização', () => {
    const screen = read('(main)/settings/openfinance/index.tsx');
    expect(screen).toMatch(/const syncAction = syncActionOf\(connection, available\);/);
    expect(screen).toMatch(/\{syncAction === 'syncNow' \? \(\s*<View style=\{styles\.syncBox\}>[\s\S]{0,400}syncNow\(connection\)/);
    expect(screen.match(/syncNow\(connection\)/g)).toHaveLength(1);
    expect(screen).toMatch(/sync\.start\(connection\.id, \{\s*force: true,/);
    expect(screen).toMatch(/\{lastSyncText\(connection\)\}/);
    expect(screen).not.toContain('próxima atualização do app');
  });

  it('Open Finance: antes da primeira sincronização a tela leva ao passo "Período" do wizard, em vez de sincronizar (revisão 1, I7)', () => {
    const screen = read('(main)/settings/openfinance/index.tsx');
    expect(screen).toMatch(/\{syncAction === 'choosePeriod' \? \(\s*<View style=\{styles\.syncBox\}>[\s\S]{0,500}choosePeriod\(connection\)/);
    expect(screen.match(/choosePeriod\(connection\)/g)).toHaveLength(1);
    // Guarda "passo 5 desta conexão" e abre o wizard, que retoma nele (startingStep, testada em wizard.test.ts).
    expect(screen).toMatch(/store\.save\(\{ step: 5, connectionId: connection\.id \}\);[\s\S]{0,120}openWizard\(\);/);
    const wizard = read('(main)/settings/openfinance/wizard.tsx');
    // No wizard, o período só é pedido (e só é enviado) enquanto a conexão nunca sincronizou.
    expect(wizard).toMatch(/setPeriodChoosable\(neverSynced\(mine\)\);/);
    expect(wizard).toMatch(/historyMonths: periodChoosable \? historyMonths : undefined,/);
    // Ao guardar as credenciais (passo 3), o período vem da mesma regra: reconectar não manda "3" por cima do
    // que foi escolhido (revisão 3, I1).
    expect(wizard).toMatch(/historyMonths: historyMonthsWithCredentials\(mine\),/);
    expect(wizard).not.toMatch(/historyMonths: 3,/);
    expect(wizard).toMatch(/\{periodChoosable \? \(\s*<View accessibilityRole="radiogroup"/);
  });

  it('Open Finance: ao voltar à tela, o resultado da sincronização anterior some; uma em andamento continua (revisão 1, I4)', () => {
    const screen = read('(main)/settings/openfinance/index.tsx');
    expect(screen).toMatch(
      /useOnRefocus\(\(\) => \{\s*if \(shouldClearSyncOnRefocus\(sync\.phase\)\) sync\.reset\(\);\s*void refetch\(\{ cancelRefetch: false \}\);\s*\}\);/,
    );
  });

  it('Open Finance: o wizard termina quando o servidor aceita o pedido, não quando a sincronização acaba (revisão 1, M1)', () => {
    const wizard = read('(main)/settings/openfinance/wizard.tsx');
    // finish() é o aviso de aceite passado a sync.start (requestAndFollowSync, testada em sync.test.ts) e não roda depois dela.
    expect(wizard).toMatch(/sync\.start\(\s*connectionId,\s*\{[\s\S]{0,200}\},[\s\S]{0,300}\(\) => void useWizardStore\.getState\(\)\.finish\(\),\s*\);/);
    expect(wizard.match(/useWizardStore\.getState\(\)\.finish\(\)/g)).toHaveLength(1);
    const hook = read('../src/modules/openfinance/useSyncRun.ts');
    expect(hook).toMatch(/requestAndFollowSync\(connectionId, options, \{[\s\S]{0,600}onAccepted,\s*\}\)/);
  });

  it('Open Finance: a linha da revisão que não pode ser confirmada mostra o motivo que vem da regra testada (revisão 1, I5)', () => {
    const review = read('(main)/openfinance/review.tsx');
    expect(review).toMatch(/const whyNot = unselectableReason\(line\);/);
    expect(review).toMatch(/\{whyNot \? <Text style=\{styles\.pendingText\}>\{whyNot\}<\/Text> : null\}/);
    // O laço dos lotes (parada no erro, o que já entrou, sessão trocada) é confirmInBatches, testada em review.test.ts.
    expect(review).toMatch(/await confirmInBatches\(batches, \{[\s\S]{0,200}isCurrent: \(\) => getSessionEpoch\(\) === epoch,\s*\}\)/);
    expect(review).not.toMatch(/for \(const batch of batches\)/);
    // O valor de cada linha sai na moeda dela (lineAmountText, testada): a tela não formata dinheiro por conta própria.
    expect(review).toMatch(/\{lineAmountText\(line\.amount, line\.currency\)\}/);
    expect(review).not.toMatch(/Intl\.NumberFormat|\|\| 'BRL'/);
  });

  it('Open Finance: a revisão do banco volta para Transações, é remontada a cada visita e usa as regras testadas', () => {
    expect(parentRouteOf('openfinance/review')).toBe('/(main)/transactions');
    const review = read('(main)/openfinance/review.tsx');
    expect(review).toMatch(/export default resetOnFocus\(BankReviewScreen\)/);
    expect(review).toMatch(/goToParent\('openfinance\/review'\)/);
    // "Selecionar tudo" e os lotes vêm de review.ts; a caixa só existe em linha selecionável.
    expect(review).toMatch(/new Set\(selectAllIds\(expenses\)\)/);
    expect(review).toMatch(/buildConfirmBatches\(expenses, selected, chosen\)/);
    expect(review).toMatch(/\{selectable \? \(\s*<TouchableOpacity[\s\S]{0,400}accessibilityRole="checkbox"/);
    expect(review).toContain('Selecionar tudo');
    expect(review).toContain('Confirmar selecionadas');
    // O valor não é editável: a tela não tem campo de texto.
    expect(review).not.toMatch(/<TextInput\b/);
  });

  it('Open Finance: o atalho da tela de Transações só é desenhado quando há algo do banco para revisar', () => {
    const transactions = read('(main)/transactions/index.tsx');
    expect(transactions).toMatch(/const bankReviewShortcut = reviewShortcutLabel\(bankReview\.pending\);/);
    expect(transactions).toMatch(/\{bankReviewShortcut !== null && \(\s*<TouchableOpacity[\s\S]{0,200}router\.push\('\/\(main\)\/openfinance\/review'/);
    expect(transactions.match(/\(main\)\/openfinance\/review/g)).toHaveLength(1);
    expect(transactions).toMatch(/useOnRefocus\(\(\) => bankReview\.refetch\(\)\)/);
  });

  it('Open Finance: o pedido silencioso ao abrir o app é ligado no layout principal, só com sessão e grupo', () => {
    expect(layout).toMatch(/useAutoSyncOnOpen\(gate === 'app'\);/);
    const hook = read('../src/modules/openfinance/useAutoSync.ts');
    expect(hook).toMatch(/registerUserDataCleaner\(/);
    expect(hook).toMatch(/getEpoch: getSessionEpoch/);
    // Só APIs do próprio React Native: nenhum módulo nativo novo.
    expect(hook).toMatch(/import \{ AppState \} from 'react-native'/);
  });

  it('Open Finance: o passo 5 do wizard manda o período escolhido e termina no botão da revisão', () => {
    const wizard = read('(main)/settings/openfinance/wizard.tsx');
    expect(wizard).toMatch(/sync\.start\(\s*connectionId,\s*\{\s*historyMonths: periodChoosable \? historyMonths : undefined,/);
    expect(wizard).toMatch(/HISTORY_OPTIONS\.map\(/);
    expect(wizard).toContain('Conectar e sincronizar');
    expect(wizard).toMatch(/reviewDoneLabel\(toReview\)/);
    expect(wizard).toMatch(/router\.push\('\/\(main\)\/openfinance\/review'/);
  });

  it('Open Finance: na tela de gestão, o texto de saldo e limite (com a data da leitura) vem da regra testada (accountBalanceText)', () => {
    const screen = read('(main)/settings/openfinance/index.tsx');
    expect(screen).toMatch(/\{accountBalanceText\(account, formatMoney\)\}/);
    expect(screen).not.toContain('Fatura atual');
    expect(screen).not.toContain('Limite disponível');
  });

  it('Open Finance: no passo 3 do wizard, o que fazer quando a conexão já existe vem da regra testada', () => {
    const wizard = read('(main)/settings/openfinance/wizard.tsx');
    expect(wizard).toMatch(/await existingConnectionAfterCreateError\(getApiErrorCode\(err\), /);
    expect(wizard).not.toContain('BANK_CONNECTION_ALREADY_EXISTS');
  });

  it('Open Finance: o wizard abre os dois sites pelo Linking do React Native, sem módulo nativo novo', () => {
    const wizard = read('(main)/settings/openfinance/wizard.tsx');
    expect(wizard).toMatch(/Linking\.openURL\(/);
    expect(wizard).toMatch(/import \{[^}]*\bLinking\b[^}]*\} from 'react-native'/);
    // O campo do segredo nunca aparece em texto aberto.
    expect(wizard).toMatch(/accessibilityLabel="Client Secret"[\s\S]{0,200}secureTextEntry/);
  });

  it('as abas usam o histórico para o botão voltar (o padrão da biblioteca pula para a primeira aba)', () => {
    expect(TABS_BACK_BEHAVIOR).toBe('history');
    expect(layout).toMatch(/<Tabs\s[^>]*backBehavior=\{TABS_BACK_BEHAVIOR\}/);
  });

  it('nenhuma aba oculta usa router.back(): o destino não pode depender do histórico', () => {
    const offenders = Object.keys(PARENT_ROUTE)
      .map((screen) => `(main)/${screen}.tsx`)
      .filter((file) => /router\.back\(\)/.test(read(file)));
    expect(offenders).toEqual([]);
  });

  it('as telas com formulário ou dados sensíveis são remontadas a cada visita', () => {
    const mustReset = [
      '(main)/transactions/edit.tsx',
      '(main)/transactions/new.tsx',
      '(main)/ocr-review.tsx',
      '(main)/settings/change-password.tsx',
      '(main)/settings/verify-email.tsx',
      '(main)/settings/capture-consent.tsx',
      '(main)/settings/openfinance/wizard.tsx',
      '(main)/openfinance/review.tsx',
    ];
    const missing = mustReset.filter((file) => !/export default resetOnFocus\(/.test(read(file)));
    expect(missing).toEqual([]);
  });

  it('a tela do grupo busca os dados de novo a cada volta e ao puxar para atualizar', () => {
    const group = read('(main)/settings/group.tsx');
    expect(group).toMatch(/useOnRefocus\(/);
    expect(group).toMatch(/<RefreshControl /);
  });

  it('a tela de envio de extrato limpa o erro antigo ao voltar, sem cancelar um envio em andamento', () => {
    expect(read('(main)/ocr-upload.tsx')).toMatch(/useOnRefocus\(\(\) => setState\(\(current\) => \(shouldResetUploadOnRevisit\(current\.phase\)/);
  });
});

describe('tela de criar/entrar em grupo', () => {
  const source = read('(auth)/couple-setup.tsx');
  // Só o que o usuário pode ler: sem comentários e sem nomes de código em inglês.
  const visible = source.replace(/\/\/.*$/gm, '').replace(/\{\/\*[\s\S]*?\*\/\}/g, '');

  it('tem como sair da conta, pelo logout compartilhado (M-I5)', () => {
    expect(source).toMatch(/import \{ logout \} from '@\/services\/logout';/);
    expect(visible).toMatch(/accessibilityLabel="Sair da conta"/);
    expect(visible).toMatch(/await logout\(\);\s*router\.replace\('\/login'/);
  });

  it('fala em "grupo" e em "Painel", nunca em "casal" ou "Dashboard" (M-M9)', () => {
    expect(visible).not.toMatch(/casal/i);
    expect(visible).not.toMatch(/dashboard/i);
    expect(visible).toMatch(/Ir para o Painel/);
    expect(visible).toMatch(/Criar grupo/);
  });
});
