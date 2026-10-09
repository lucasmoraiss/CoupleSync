// Issue #3: o app avisa quando há APK novo. Regras puras: comparação de versões e o que cada tela mostra.
import {
  APK_DOWNLOAD_URL,
  APP_UPDATE_TEXT,
  appUpdateState,
  blocksApp,
  compareVersions,
  decideUpdate,
  parseVersion,
  settingsVersionRow,
  showDashboardNotice,
} from '../appUpdate';

const server = (latestVersion: unknown, minimumVersion: unknown = null, downloadUrl: unknown = APK_DOWNLOAD_URL) => ({
  latestVersion,
  minimumVersion,
  downloadUrl,
});

describe('comparação de versões (B3)', () => {
  it('1.1.0 é maior que 1.0.0', () => {
    expect(compareVersions('1.1.0', '1.0.0')).toBe(1);
    expect(compareVersions('1.0.0', '1.1.0')).toBe(-1);
  });

  it('o prefixo "v" não conta: v1.1.0 é igual a 1.1.0', () => {
    expect(compareVersions('v1.1.0', '1.1.0')).toBe(0);
    expect(compareVersions('V1.1.0', 'v1.1.0')).toBe(0);
  });

  it('o sufixo não conta: 1.0.0-pit é igual a 1.0.0', () => {
    expect(compareVersions('1.0.0-pit', '1.0.0')).toBe(0);
    expect(compareVersions('v1.0.0-pit', '1.0.0+7')).toBe(0);
  });

  it('compara números, não texto: 1.10.0 é maior que 1.9.0', () => {
    expect(compareVersions('1.10.0', '1.9.0')).toBe(1);
    expect(compareVersions('1.9.0', '1.10.0')).toBe(-1);
    expect(compareVersions('2.0.0', '1.99.99')).toBe(1);
    expect(compareVersions('1.0.10', '1.0.9')).toBe(1);
  });

  it.each([
    ['', '1.0.0'],
    ['1.0.0', ''],
    ['abc', '1.0.0'],
    ['1.0', '1.0.0'],
    ['1.0.0.1', '1.0.0'],
    ['1.0.x', '1.0.0'],
    ['1.0.0pit', '1.0.0'],
    ['1,0,0', '1.0.0'],
    ['-1.0.0', '1.0.0'],
    [null, '1.0.0'],
    [undefined, '1.0.0'],
    [110, '1.0.0'],
    [{ version: '1.0.0' }, '1.0.0'],
    ['1.0.0', null],
    ['1.0.99999999999999999999', '1.0.0'],
  ])('texto inválido (%p x %p) dá "desconhecida" (null), sem exceção', (a, b) => {
    expect(() => compareVersions(a, b)).not.toThrow();
    expect(compareVersions(a, b)).toBeNull();
  });

  it('parseVersion devolve os três números, ou null', () => {
    expect(parseVersion(' v1.10.3-pit ')).toEqual([1, 10, 3]);
    expect(parseVersion('01.002.0003')).toEqual([1, 2, 3]);
    expect(parseVersion('1.1')).toBeNull();
    expect(parseVersion(undefined)).toBeNull();
  });
});

describe('decisão: nenhum | aviso | obrigatorio', () => {
  it('instalada menor que a última, sem mínima: aviso', () => {
    expect(decideUpdate({ installed: '1.0.0', latest: '1.1.0', minimum: null })).toBe('aviso');
  });

  it('instalada igual ou maior que a última: nenhum', () => {
    expect(decideUpdate({ installed: '1.1.0', latest: '1.1.0', minimum: null })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.2.0', latest: '1.1.0', minimum: null })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.1.0', latest: 'v1.1.0', minimum: '1.1.0' })).toBe('nenhum');
  });

  it('instalada menor que a mínima e que a última: obrigatorio', () => {
    expect(decideUpdate({ installed: '1.0.0', latest: '1.2.0', minimum: '1.1.0' })).toBe('obrigatorio');
    expect(decideUpdate({ installed: '1.0.0', latest: '1.1.0', minimum: '1.1.0' })).toBe('obrigatorio');
  });

  // Uma mínima válida mas errada (11.0.0 no lugar de 1.1.0, ou criada antes de o APK sair) não tranca ninguém:
  // "Baixar" entregaria o mesmo APK que a pessoa já tem.
  it('quem está na última publicada (ou acima) nunca é bloqueado, qualquer que seja a mínima', () => {
    expect(decideUpdate({ installed: '1.1.0', latest: '1.1.0', minimum: '11.0.0' })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.2.0', latest: '1.1.0', minimum: '11.0.0' })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.1.0', latest: 'v1.1.0', minimum: '1.2.0' })).toBe('nenhum');
  });

  it('com mínima acima da última, quem está abaixo da última ainda é bloqueado: o APK da última o destrava', () => {
    expect(decideUpdate({ installed: '1.0.0', latest: '1.1.0', minimum: '11.0.0' })).toBe('obrigatorio');
  });

  it('sem saber a última ninguém é bloqueado: não há como garantir que existe APK que destrave', () => {
    expect(decideUpdate({ installed: '1.0.0', latest: null, minimum: '1.1.0' })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.0.0', latest: 'nova', minimum: '1.1.0' })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.0.0', latest: undefined, minimum: '1.1.0' })).toBe('nenhum');
  });

  it('instalada igual à mínima e menor que a última: só aviso', () => {
    expect(decideUpdate({ installed: '1.1.0', latest: '1.2.0', minimum: '1.1.0' })).toBe('aviso');
  });

  it('instalada desconhecida: nunca avisa nem bloqueia', () => {
    expect(decideUpdate({ installed: null, latest: '9.9.9', minimum: '9.9.9' })).toBe('nenhum');
    expect(decideUpdate({ installed: 'desconhecida', latest: '9.9.9', minimum: '9.9.9' })).toBe('nenhum');
  });

  it('última e mínima desconhecidas ou inválidas: nenhum', () => {
    expect(decideUpdate({ installed: '1.0.0', latest: null, minimum: null })).toBe('nenhum');
    expect(decideUpdate({ installed: '1.0.0', latest: 'nova', minimum: 'antiga' })).toBe('nenhum');
  });
});

describe('B4: instalada 1.0.0, última 1.1.0, mínima null', () => {
  const state = appUpdateState('1.0.0', server('1.1.0'));

  it('o Painel mostra o aviso', () => {
    expect(state.decision).toBe('aviso');
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: null })).toBe(true);
    expect(APP_UPDATE_TEXT.notice).toBe('Há uma versão nova do app');
    expect(APP_UPDATE_TEXT.download).toBe('Baixar');
    expect(APP_UPDATE_TEXT.notNow).toBe('Agora não');
  });

  it('Configurações mostra a versão instalada e "Atualizar o app"', () => {
    expect(settingsVersionRow(state)).toEqual({ versionText: 'Versão do app: 1.0.0', showUpdate: true });
    expect(APP_UPDATE_TEXT.updateApp).toBe('Atualizar o app');
  });

  it('ninguém é bloqueado', () => {
    expect(blocksApp(state)).toBe(false);
  });
});

describe('B5: instalada igual ou maior que a última', () => {
  it.each(['1.1.0', '1.2.0'])('instalada %s, última 1.1.0: nenhum aviso; Configurações mostra só a versão', (installed) => {
    const state = appUpdateState(installed, server('1.1.0'));

    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: null })).toBe(false);
    expect(settingsVersionRow(state)).toEqual({ versionText: `Versão do app: ${installed}`, showUpdate: false });
    expect(blocksApp(state)).toBe(false);
  });

  it('a versão é mostrada como está instalada, sem o "v" nem o sufixo', () => {
    expect(settingsVersionRow(appUpdateState('v1.0.0-pit', server('1.0.0'))).versionText).toBe('Versão do app: 1.0.0');
  });
});

describe('B6: "Agora não"', () => {
  const state = appUpdateState('1.0.0', server('1.1.0'));

  it('esconde o aviso do Painel para a versão dispensada', () => {
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: '1.1.0' })).toBe(false);
  });

  it('o aviso volta quando a última versão aumenta', () => {
    const newer = appUpdateState('1.0.0', server('1.2.0'));
    expect(showDashboardNotice(newer, { loaded: true, dismissedVersion: '1.1.0' })).toBe(true);
    expect(showDashboardNotice(newer, { loaded: true, dismissedVersion: '1.2.0' })).toBe(false);
  });

  it('a linha de Configurações continua, dispensado ou não', () => {
    expect(settingsVersionRow(state).showUpdate).toBe(true);
  });

  it('antes de saber o que foi dispensado neste aparelho o aviso não aparece (não pisca)', () => {
    expect(showDashboardNotice(state, { loaded: false, dismissedVersion: null })).toBe(false);
  });

  it('um valor guardado que não é versão não esconde nada', () => {
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: 'lixo' })).toBe(true);
  });
});

describe('B7: instalada menor que a mínima', () => {
  const state = appUpdateState('1.0.0', server('1.2.0', '1.1.0'));

  it('o app é bloqueado, com os textos da tela de bloqueio', () => {
    expect(state.decision).toBe('obrigatorio');
    expect(blocksApp(state)).toBe(true);
    expect(APP_UPDATE_TEXT.required).toBe('Esta versão não é mais aceita. Baixe a nova.');
    expect(APP_UPDATE_TEXT.signOut).toBe('Sair da conta');
  });

  it('o bloqueio não pode ser dispensado: "Agora não" de antes não o desfaz', () => {
    expect(blocksApp(state)).toBe(true);
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: '1.2.0' })).toBe(false);
  });
});

describe('B8: versão instalada desconhecida (módulo nativo ausente)', () => {
  it.each([null, '', 'desconhecida'])('instalada %p: nenhum aviso, nenhum bloqueio, nenhuma linha de versão', (installed) => {
    const state = appUpdateState(installed, server('9.9.9', '9.9.9'));

    expect(state.decision).toBe('nenhum');
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: null })).toBe(false);
    expect(blocksApp(state)).toBe(false);
    expect(settingsVersionRow(state)).toEqual({ versionText: null, showUpdate: false });
  });
});

describe('B9: rota fora do ar, 5xx ou campos null', () => {
  it.each([
    ['sem resposta (rota fora do ar, 5xx, 404 de servidor antigo)', undefined],
    ['null', null],
    ['campos null', server(null, null)],
    ['objeto vazio', {}],
    ['texto', '<html>erro</html>'],
    ['lista', []],
    ['campos de outro tipo', server(110, true, 42)],
    ['campos que não são versão', server('nova', 'antiga')],
  ])('%s: nenhum aviso e nenhum bloqueio', (_name, data) => {
    const state = appUpdateState('1.0.0', data);

    expect(state.decision).toBe('nenhum');
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: null })).toBe(false);
    expect(blocksApp(state)).toBe(false);
    expect(settingsVersionRow(state)).toEqual({ versionText: 'Versão do app: 1.0.0', showUpdate: false });
  });

  it('só a mínima veio null: avisa, não bloqueia', () => {
    expect(appUpdateState('1.0.0', server('1.1.0', null)).decision).toBe('aviso');
  });
});

describe('B10: o link de download', () => {
  it('é o link fixo do APK mais recente', () => {
    expect(APK_DOWNLOAD_URL).toBe('https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk');
    expect(appUpdateState('1.0.0', server('1.1.0')).downloadUrl).toBe(APK_DOWNLOAD_URL);
  });

  it('um endereço do servidor só vale se for das Releases do próprio repositório, em https', () => {
    const other = 'https://github.com/lucasmoraiss/CoupleSync/releases/download/v1.2.0/couplesync.apk';
    expect(appUpdateState('1.0.0', server('1.2.0', null, other)).downloadUrl).toBe(other);
  });

  it.each([
    [undefined],
    [null],
    [''],
    [42],
    ['http://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk'],
    ['https://exemplo.test/outro.apk'],
    ['https://github.com/outra-pessoa/CoupleSync/releases/latest/download/couplesync.apk'],
    ['https://github.com/lucasmoraiss/CoupleSync/releases/../../../outra-pessoa/outro/releases/download/x.apk'],
    ['https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk?next=https://exemplo.test'],
    ['https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/a b.apk'],
    ['https://github.com.exemplo.test/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk'],
    ['javascript:alert(1)'],
  ])('qualquer outro endereço vindo do servidor (%p) é trocado pelo link fixo', (downloadUrl) => {
    expect(appUpdateState('1.0.0', server('1.1.0', null, downloadUrl)).downloadUrl).toBe(APK_DOWNLOAD_URL);
  });
});
