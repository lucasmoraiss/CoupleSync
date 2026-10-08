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
  // Uma aba oculta pode ter título (o Assistente): continua sendo aba oculta, com tela-mãe.
  const hiddenScreens = [...layout.matchAll(/<Tabs\.Screen name="([^"]+)" options=\{\{ (?:title: '[^']*', )?href: null \}\} \/>/g)].map((m) => m[1]);

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
