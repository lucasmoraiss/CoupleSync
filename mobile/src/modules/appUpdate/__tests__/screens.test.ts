// Issue #3: as telas só ligam as regras testadas em appUpdate.test.ts ao React. Aqui se confere essa ligação,
// lendo o código das telas (o Jest deste projeto não monta componentes), e o botão "Baixar" de verdade (B10).
import * as fs from 'fs';
import * as path from 'path';

const mockOpenURL = jest.fn();
jest.mock('react-native', () => ({ Linking: { openURL: (url: string) => mockOpenURL(url) } }));

import { APK_DOWNLOAD_URL, appUpdateState, settingsVersionRow } from '../appUpdate';
import { openApkDownload } from '../openDownload';

const MOBILE = path.resolve(__dirname, '../../../..');
const read = (relative: string) => fs.readFileSync(path.join(MOBILE, relative), 'utf8');

const painel = read('app/(main)/index.tsx');
const settings = read('app/(main)/settings/index.tsx');
const layout = read('app/(main)/_layout.tsx');
const banner = read('src/components/AppUpdateBanner.tsx');
const blockScreen = read('src/components/AppUpdateRequiredScreen.tsx');
const hook = read('src/modules/appUpdate/useAppUpdate.ts');
const client = read('src/services/apiClient.ts');

beforeEach(() => mockOpenURL.mockReset());

describe('B10: "Baixar" abre exatamente o link fixo', () => {
  it('com a resposta normal do servidor', async () => {
    mockOpenURL.mockResolvedValue(true);
    const state = appUpdateState('1.0.0', { latestVersion: '1.1.0', minimumVersion: null, downloadUrl: APK_DOWNLOAD_URL });

    await openApkDownload(state.downloadUrl, jest.fn());

    expect(mockOpenURL).toHaveBeenCalledTimes(1);
    expect(mockOpenURL).toHaveBeenCalledWith('https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk');
  });

  it('e também quando o servidor não manda o endereço', async () => {
    mockOpenURL.mockResolvedValue(true);
    const state = appUpdateState('1.0.0', { latestVersion: '1.1.0', minimumVersion: '1.1.0' });

    await openApkDownload(state.downloadUrl, jest.fn());

    expect(mockOpenURL).toHaveBeenCalledWith('https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk');
  });

  it('se nenhum navegador abrir, a tela avisa com o endereço para abrir à mão (sem exceção)', async () => {
    mockOpenURL.mockRejectedValue(new Error('No Activity found to handle Intent'));
    const onFailure = jest.fn();

    await expect(openApkDownload(APK_DOWNLOAD_URL, onFailure)).resolves.toBeUndefined();

    expect(onFailure).toHaveBeenCalledTimes(1);
    expect(onFailure.mock.calls[0][0]).toContain('github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk');
  });

  it('o aviso, a linha de Configurações e a tela de bloqueio abrem o link da regra, por openApkDownload', () => {
    for (const source of [banner, settings, blockScreen]) {
      expect(source).toMatch(/openApkDownload\((update|state)\.downloadUrl, /);
      expect(source).not.toMatch(/Linking\.openURL/);
      expect(source).not.toContain('releases/latest');
    }
  });
});

describe('Painel (B4, B5, B6)', () => {
  it('o Painel monta o aviso', () => {
    expect(painel).toMatch(/import \{ AppUpdateBanner \} from '@\/components\/AppUpdateBanner';/);
    expect(painel).toMatch(/<AppUpdateBanner \/>/);
  });

  it('o aviso aparece só quando a regra testada manda, com os textos da regra', () => {
    expect(banner).toMatch(/if \(!showDashboardNotice\(update, dismissal\)\) return null;/);
    expect(banner).toContain('{APP_UPDATE_TEXT.notice}');
    expect(banner).toContain('{APP_UPDATE_TEXT.download}');
    expect(banner).toContain('{APP_UPDATE_TEXT.notNow}');
  });

  it('"Agora não" dispensa a última versão publicada (não a instalada)', () => {
    expect(banner).toMatch(/dismiss\(update\.latestVersion\)/);
  });

  it('o Painel fica montado entre visitas: ao voltar o foco a versão é consultada de novo', () => {
    expect(banner).toMatch(/useOnRefocus\(\(\) => update\.refetchIfStale\(\)\);/);
    expect(hook).toMatch(/refetchIfStale: \(\) => \{\s*if \(query\.isStale\) void query\.refetch\(\);/);
  });
});

describe('Configurações (B4, B5, B6)', () => {
  it('a linha de versão e o "Atualizar o app" vêm da regra testada', () => {
    expect(settings).toMatch(/const versionRow = settingsVersionRow\(update\);/);
    expect(settings).toMatch(/\{versionRow\.versionText \?/);
    expect(settings).toMatch(/\{versionRow\.showUpdate \?/);
    expect(settings).toContain('{APP_UPDATE_TEXT.updateApp}');
  });

  // A única prova automática de que a versão nativa é lida num APK de verdade: o App E2E afirma a linha.
  it('o fluxo de tela 03 afirma a linha de versão por expressão regular que vale para qualquer versão', () => {
    const flow = read('tests/e2e/flows/03-entrar-em-grupo.yaml');
    const line = /element: "(Versão do app: [^"]+)"/.exec(flow);
    expect(line).not.toBeNull();
    // Entre aspas duplas do YAML, `\\.` é `\.`; o Maestro compara o texto inteiro com a expressão.
    const pattern = new RegExp(`^${line![1].replace(/\\\\/g, '\\')}$`);
    for (const installed of ['1.1.0', '1.2.0', '10.20.30']) {
      expect(settingsVersionRow(appUpdateState(installed, undefined)).versionText).toMatch(pattern);
    }
    expect('Versão do app: ').not.toMatch(pattern);
    expect(settings).toMatch(/<Text style=\{styles\.versionText\}>\{versionRow\.versionText\}<\/Text>/);
  });

  it('a linha não pode ser dispensada: Configurações nem lê o "Agora não"', () => {
    expect(settings).not.toContain('useAppUpdateDismissal');
    expect(settings).not.toContain('showDashboardNotice');
  });
});

describe('bloqueio por versão mínima (B7)', () => {
  it('o layout das abas devolve a tela de bloqueio ANTES de montar qualquer aba', () => {
    const block = layout.indexOf('<AppUpdateRequiredScreen');
    const tabs = layout.indexOf('<Tabs');
    expect(layout).toMatch(/const blocked = gate === 'app' && blocksApp\(update\);/);
    expect(layout).toMatch(/if \(blocked\) \{\s*return <AppUpdateRequiredScreen update=\{update\} \/>;/);
    expect(block).toBeGreaterThan(-1);
    expect(block).toBeLessThan(tabs);
  });

  it('a tela de bloqueio tem "Baixar" e "Sair da conta", com os textos da regra, e não pode ser dispensada', () => {
    expect(blockScreen).toContain('{APP_UPDATE_TEXT.required}');
    expect(blockScreen).toContain('{APP_UPDATE_TEXT.download}');
    expect(blockScreen).toContain('{APP_UPDATE_TEXT.signOut}');
    expect(blockScreen).toMatch(/await logout\(\);/);
    expect(blockScreen).not.toContain('notNow');
    expect(blockScreen).not.toContain('dismiss');
  });

  it('os portões de sessão e de grupo vêm antes do bloqueio e continuam redirecionando', () => {
    const gateReturn = layout.indexOf("if (gate !== 'app' || update.waitingForMemory) {");
    expect(gateReturn).toBeGreaterThan(-1);
    expect(gateReturn).toBeLessThan(layout.indexOf('if (blocked) {'));
    expect(layout).toMatch(/if \(gate === 'login'\) \{\s*router\.replace\('\/login' as any\);\s*\} else if \(gate === 'group-setup'\) \{/);
  });

  it('sob o bloqueio o layout não dispara nada do grupo: sincronização do Open Finance, token de push, pergunta da captura', () => {
    expect(layout).toMatch(/const appOpen = gate === 'app' && !blocked && !update\.waitingForMemory;/);
    expect(layout).toMatch(/useAutoSyncOnOpen\(appOpen\);/);
    expect(layout).toMatch(/if \(appOpen\) \{\s*registerPushToken\(\);/);
    expect(layout).not.toMatch(/gate === 'app'\) \{\s*registerPushToken/);
    // A pergunta única da captura não pode ser gasta numa abertura que vai dar no bloqueio.
    expect(layout).toMatch(/useCaptureConsentSync\(!blocked && !update\.waitingForMemory\);/);
    const capture = read('src/modules/integrations/notification-capture/useCaptureConsentSync.ts');
    expect(capture).toMatch(/if \(!decision\.prompt \|\| !canPrompt\) return;/);
  });

  it('o aviso do Painel ocupa uma linha só (não empurra os números do mês para fora da primeira tela)', () => {
    expect(banner).toMatch(/banner: \{[^}]*flexDirection: 'row'/);
    expect(painel.indexOf('<AppUpdateBanner />')).toBeGreaterThan(painel.indexOf('<EmailVerificationBanner />'));
  });

  it('na abertura o layout espera a lembrança do aparelho (local) antes de montar qualquer aba, nunca a rede', () => {
    const waiting = layout.indexOf("if (gate !== 'app' || update.waitingForMemory) {");
    expect(waiting).toBeGreaterThan(-1);
    expect(waiting).toBeLessThan(layout.indexOf('if (blocked) {'));
    expect(waiting).toBeLessThan(layout.indexOf('<Tabs'));
    // A decisão vem da regra testada (resposta do servidor, senão a lembrança), com a versão instalada atual.
    expect(hook).toMatch(/resolveUpdate\(\{ installed: getInstalledVersion\(\), live: query\.data, memory \}\)/);
    expect(hook).toMatch(/void loadMemory\(\);/);
    expect(hook).toMatch(/if \(query\.data !== undefined\) void remember\(query\.data\);/);
    expect(layout).not.toMatch(/isLoading|isFetching|isPending/);
  });

  it('a tela de bloqueio reconsulta sozinha (primeiro plano e intervalo) e para ao sair', () => {
    expect(blockScreen).toMatch(
      /useEffect\(\s*\(\) =>\s*startBlockRecheck\(\{\s*recheck: \(\) => recheckRef\.current\(\),\s*onAppStateChange: \(listener\) => AppState\.addEventListener\('change', listener\),\s*\}\),\s*\[\],\s*\);/,
    );
    expect(blockScreen).toMatch(/recheckRef\.current = update\.refetch;/);
    expect(layout).toMatch(/<AppUpdateRequiredScreen update=\{update\} \/>/);
    expect(hook).toMatch(/refetch: \(\) => \{\s*void query\.refetch\(\);\s*\}/);
  });

  it('"Sair da conta" no bloqueio pede a mesma confirmação de Configurações (sair encerra os outros aparelhos)', () => {
    const confirmation = /Alert\.alert\('Sair', 'Deseja realmente sair da conta\? Você também será desconectado dos outros aparelhos em que usa esta conta\.', \[\s*\{ text: 'Cancelar', style: 'cancel' \},\s*\{\s*text: 'Sair',\s*style: 'destructive',/;
    expect(settings).toMatch(confirmation);
    expect(blockScreen).toMatch(confirmation);
    // Só o botão do diálogo sai: tocar em "Sair da conta" não chama logout direto.
    expect(blockScreen).not.toMatch(/onPress=\{(handleSignOut|signOut)\}/);
    expect(blockScreen).toMatch(/onPress=\{confirmSignOut\}/);
  });

  it('a tela de bloqueio não faz nenhuma consulta (nem do grupo, nem outra)', () => {
    expect(blockScreen).not.toMatch(/useQuery|useMutation|ApiClient|apiClient/);
  });
});

describe('consulta da versão (B9)', () => {
  it('usa o cliente da API e o TanStack Query', () => {
    expect(hook).toMatch(/import \{ useQuery \} from '@tanstack\/react-query';/);
    expect(hook).toMatch(/appApiClient\.getVersion\(\)/);
  });

  // A API no plano gratuito leva dezenas de segundos para acordar: é a abertura típica do app.
  it('com a API dormindo a consulta insiste como as outras do app: sem tempo limite nem repetição próprios', () => {
    expect(hook).not.toMatch(/retry\s*:/);
    expect(client).toMatch(/axiosInstance\.get<AppVersionResponse>\('\/api\/v1\/app\/version'\),/);
    expect(read('src/services/queryClient.ts')).toMatch(/queries: \{ retry: 2,/);
    expect(client).toMatch(/axios\.create\(\{[\s\S]{0,200}?timeout: 30000,/);
  });

  it('a falha continua silenciosa: nada de aviso, alerta ou erro na tela', () => {
    expect(hook).not.toMatch(/showToast|Alert|isError|onError/);
  });

  it('o que as telas recebem é sempre o resultado da regra: sem dado (erro, 5xx, rota ausente) não há aviso', () => {
    expect(hook).toMatch(/\.\.\.resolveUpdate\(\{ installed: getInstalledVersion\(\), live: query\.data, memory \}\)/);
  });

  it('a rota é a nova, anônima, e não muda nenhuma chamada que o app já fazia', () => {
    expect(client).toMatch(/axiosInstance\.get<AppVersionResponse>\('\/api\/v1\/app\/version'/);
  });

  it('"dispensei a versão X" não é dado do usuário: o módulo não se registra na limpeza de sair da conta', () => {
    const store = read('src/modules/appUpdate/dismissalStore.ts');
    expect(store).not.toMatch(/import[^;]*registerUserDataCleaner/);
    expect(store).not.toMatch(/registerUserDataCleaner\(/);
  });
});
