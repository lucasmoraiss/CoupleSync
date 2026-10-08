// Issue #3 (B6): "Agora não" guarda a versão dispensada NESTE APARELHO. Não é dado do usuário: sair da conta não apaga.
const secureStore: Record<string, string> = {};
let failReads = false;
let failWrites = false;

jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    if (failWrites) throw new Error('keystore indisponível');
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn(async (key: string) => {
    if (failReads) throw new Error('keystore indisponível');
    return secureStore[key] ?? null;
  }),
  deleteItemAsync: jest.fn(async (key: string) => {
    delete secureStore[key];
  }),
}));

import { useSessionStore } from '@/state/sessionStore';
import { clearUserData } from '@/state/userData';
import { appUpdateState, showDashboardNotice } from '../appUpdate';
import { DISMISSED_VERSION_KEY, useAppUpdateDismissal } from '../dismissalStore';

const store = () => useAppUpdateDismissal.getState();
const serverSays = (latestVersion: string) => ({ latestVersion, minimumVersion: null, downloadUrl: null });
const noticeFor = (latestVersion: string) => showDashboardNotice(appUpdateState('1.0.0', serverSays(latestVersion)), store());

beforeEach(() => {
  failReads = false;
  failWrites = false;
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  store().resetForTests();
});

describe('"Agora não" (B6)', () => {
  it('esconde o aviso para a versão dispensada e grava no aparelho', async () => {
    await store().load();
    expect(noticeFor('1.1.0')).toBe(true);

    await store().dismiss('1.1.0');

    expect(noticeFor('1.1.0')).toBe(false);
    expect(secureStore[DISMISSED_VERSION_KEY]).toBe('1.1.0');
  });

  it('o aviso some na hora, antes de a gravação terminar', async () => {
    await store().load();

    const saving = store().dismiss('1.1.0');

    expect(noticeFor('1.1.0')).toBe(false);
    await saving;
  });

  it('continua escondido depois de fechar e reabrir o app', async () => {
    await store().load();
    await store().dismiss('1.1.0');

    store().resetForTests(); // como se o app tivesse sido fechado
    expect(noticeFor('1.1.0')).toBe(false); // ainda não leu: não mostra (não pisca)
    await store().load();

    expect(store().dismissedVersion).toBe('1.1.0');
    expect(noticeFor('1.1.0')).toBe(false);
  });

  it('o aviso volta quando sai uma versão maior que a dispensada', async () => {
    await store().load();
    await store().dismiss('1.1.0');

    expect(noticeFor('1.2.0')).toBe(true);
  });

  it('sair da conta não apaga: é do aparelho, não do usuário', async () => {
    await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', 'couple-1');
    await store().load();
    await store().dismiss('1.1.0');

    await clearUserData();

    expect(useSessionStore.getState().accessToken).toBeNull();
    expect(store().dismissedVersion).toBe('1.1.0');
    expect(secureStore[DISMISSED_VERSION_KEY]).toBe('1.1.0');
    expect(noticeFor('1.1.0')).toBe(false);
  });

  it('armazenamento indisponível na leitura: o aviso aparece (nada dispensado), sem exceção', async () => {
    failReads = true;

    await expect(store().load()).resolves.toBeUndefined();

    expect(store().loaded).toBe(true);
    expect(noticeFor('1.1.0')).toBe(true);
  });

  it('armazenamento indisponível na gravação: fica escondido enquanto o app estiver aberto, sem exceção', async () => {
    await store().load();
    failWrites = true;

    await expect(store().dismiss('1.1.0')).resolves.toBeUndefined();

    expect(noticeFor('1.1.0')).toBe(false);
  });

  it('uma leitura lenta não desfaz um "Agora não" dado enquanto ela corria', async () => {
    secureStore[DISMISSED_VERSION_KEY] = '1.0.5';
    const loading = store().load();
    await store().dismiss('1.1.0');
    await loading;

    expect(store().dismissedVersion).toBe('1.1.0');
  });
});
