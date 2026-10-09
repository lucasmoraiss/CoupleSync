// Issue #3: as telas só ligam as regras testadas em appUpdate.test.ts ao React. Aqui se confere essa ligação,
// lendo o código das telas (o Jest deste projeto não monta componentes), e o botão "Baixar" de verdade (B10).
import * as fs from 'fs';
import * as path from 'path';

const mockOpenURL = jest.fn();
jest.mock('react-native', () => ({ Linking: { openURL: (url: string) => mockOpenURL(url) } }));

import { APK_DOWNLOAD_URL, appUpdateState } from '../appUpdate';
import { openApkDownload } from '../openDownload';

const MOBILE = path.resolve(__dirname, '../../../..');
const read = (relative: string) => fs.readFileSync(path.join(MOBILE, relative), 'utf8');

const painel = read('app/(main)/index.tsx');
const settings = read('app/(main)/settings/index.tsx');
const layout = read('app/(main)/_layout.tsx');
const banner = read('src/components/AppUpdateBanner.tsx');
const blockScreen = read('src/components/AppUpdateRequiredScreen.tsx');
const hook = read('src/modules/appUpdate/useAppUpdate.ts');

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
    const gateReturn = layout.indexOf("if (gate !== 'app') {");
    expect(gateReturn).toBeGreaterThan(-1);
    expect(gateReturn).toBeLessThan(layout.indexOf('if (blocked) {'));
    expect(layout).toMatch(/if \(gate === 'login'\) \{\s*router\.replace\('\/login' as any\);\s*\} else if \(gate === 'group-setup'\) \{/);
  });

  it('sob o bloqueio o layout não dispara nada do grupo: sincronização do Open Finance, token de push, pergunta da captura', () => {
    expect(layout).toMatch(/const appOpen = gate === 'app' && !blocked;/);
    expect(layout).toMatch(/useAutoSyncOnOpen\(appOpen\);/);
    expect(layout).toMatch(/if \(appOpen\) \{\s*registerPushToken\(\);/);
    expect(layout).not.toMatch(/gate === 'app'\) \{\s*registerPushToken/);
    expect(layout).toMatch(/useCaptureConsentSync\(!blocked\);/);
    const capture = read('src/modules/integrations/notification-capture/useCaptureConsentSync.ts');
    expect(capture).toMatch(/if \(!decision\.prompt \|\| !canPrompt\) return;/);
  });

  it('o aviso do Painel ocupa uma linha só (não empurra os números do mês para fora da primeira tela)', () => {
    expect(banner).toMatch(/banner: \{[^}]*flexDirection: 'row'/);
    expect(painel.indexOf('<AppUpdateBanner />')).toBeGreaterThan(painel.indexOf('<EmailVerificationBanner />'));
  });

  it('a tela de bloqueio não faz nenhuma consulta (nem do grupo, nem outra)', () => {
    expect(blockScreen).not.toMatch(/useQuery|useMutation|ApiClient|apiClient/);
  });
});

describe('consulta da versão (B9)', () => {
  it('usa o cliente da API e o TanStack Query, sem repetir tentativas', () => {
    expect(hook).toMatch(/import \{ useQuery \} from '@tanstack\/react-query';/);
    expect(hook).toMatch(/appApiClient\.getVersion\(\)/);
    expect(hook).toMatch(/retry: false/);
  });

  it('o que as telas recebem é sempre o resultado da regra: sem dado (erro, 5xx, rota ausente) não há aviso', () => {
    expect(hook).toMatch(/appUpdateState\(getInstalledVersion\(\), query\.data\)/);
  });

  it('a rota é a nova, anônima, e não muda nenhuma chamada que o app já fazia', () => {
    const client = read('src/services/apiClient.ts');
    expect(client).toMatch(/axiosInstance\.get<AppVersionResponse>\('\/api\/v1\/app\/version'/);
  });

  it('"dispensei a versão X" não é dado do usuário: o módulo não se registra na limpeza de sair da conta', () => {
    const store = read('src/modules/appUpdate/dismissalStore.ts');
    expect(store).not.toMatch(/import[^;]*registerUserDataCleaner/);
    expect(store).not.toMatch(/registerUserDataCleaner\(/);
  });
});
