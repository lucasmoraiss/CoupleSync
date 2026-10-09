// Issue #3 (B8): a versão nativa instalada é lida do módulo ExpoApplication COM GUARDA. O JavaScript novo chega por
// OTA a APKs antigos: se o módulo não existir (ou falhar), a versão é "desconhecida" (null), nunca uma exceção.
const mockRequireOptionalNativeModule = jest.fn();

jest.mock('expo-modules-core', () => ({
  requireOptionalNativeModule: (name: string) => mockRequireOptionalNativeModule(name),
}));

import { getInstalledVersion, readInstalledVersion, resetInstalledVersionForTests } from '../installedVersion';
import { appUpdateState, blocksApp, showDashboardNotice } from '../appUpdate';

beforeEach(() => {
  mockRequireOptionalNativeModule.mockReset();
  resetInstalledVersionForTests();
});

describe('leitura da versão nativa instalada', () => {
  it('vem de nativeApplicationVersion do módulo ExpoApplication (o versionName do APK)', () => {
    mockRequireOptionalNativeModule.mockReturnValue({ nativeApplicationVersion: '1.0.0', nativeBuildVersion: '7' });

    expect(getInstalledVersion()).toBe('1.0.0');
    expect(mockRequireOptionalNativeModule).toHaveBeenCalledWith('ExpoApplication');
  });

  it('módulo nativo ausente (APK sem ele): desconhecida, sem exceção', () => {
    mockRequireOptionalNativeModule.mockReturnValue(null);

    expect(() => getInstalledVersion()).not.toThrow();
    expect(getInstalledVersion()).toBeNull();
  });

  it('a procura do módulo estourando: desconhecida, sem exceção', () => {
    mockRequireOptionalNativeModule.mockImplementation(() => {
      throw new Error("Cannot find native module 'ExpoApplication'");
    });

    expect(() => getInstalledVersion()).not.toThrow();
    expect(getInstalledVersion()).toBeNull();
  });

  it('a leitura da propriedade estourando: desconhecida, sem exceção', () => {
    const broken = {
      get nativeApplicationVersion(): string {
        throw new Error('JSI');
      },
    };

    expect(readInstalledVersion(() => broken)).toBeNull();
  });

  it.each([[undefined], [null], [''], ['   '], [110], [{ versionName: '1.0.0' }]])(
    'módulo presente com valor que não serve (%p): desconhecida',
    (value) => {
      expect(readInstalledVersion(() => ({ nativeApplicationVersion: value }))).toBeNull();
    },
  );

  it('a versão não muda com o app aberto: o módulo é consultado uma vez só', () => {
    mockRequireOptionalNativeModule.mockReturnValue({ nativeApplicationVersion: '1.1.0' });

    getInstalledVersion();
    getInstalledVersion();

    expect(mockRequireOptionalNativeModule).toHaveBeenCalledTimes(1);
  });
});

describe('B8: sem o módulo nativo, com versão nova e versão mínima publicadas', () => {
  it('nenhum aviso, nenhum bloqueio, nenhuma exceção', () => {
    mockRequireOptionalNativeModule.mockReturnValue(null);

    const state = appUpdateState(getInstalledVersion(), { latestVersion: '9.9.9', minimumVersion: '9.9.9', downloadUrl: 'x' });

    expect(state.decision).toBe('nenhum');
    expect(showDashboardNotice(state, { loaded: true, dismissedVersion: null })).toBe(false);
    expect(blocksApp(state)).toBe(false);
  });
});
