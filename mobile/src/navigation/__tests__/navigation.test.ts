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
