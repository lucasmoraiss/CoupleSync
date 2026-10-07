// Open Finance (issue #24): o progresso do wizard é do USUÁRIO logado, fica no armazenamento seguro numa chave
// por usuário e some quando ele sai da conta. O Client ID e o Client Secret nunca são gravados.
const secureStore: Record<string, string> = {};
let failWrites = false;
let pendingWrite: { release: () => void } | null = null;
let holdNextWrite = false;

jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    if (failWrites) throw new Error('keystore indisponível');
    if (holdNextWrite) {
      holdNextWrite = false;
      await new Promise<void>((resolve) => {
        pendingWrite = { release: resolve };
      });
    }
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn(async (key: string) => secureStore[key] ?? null),
  deleteItemAsync: jest.fn(async (key: string) => {
    delete secureStore[key];
  }),
}));

import { useSessionStore } from '@/state/sessionStore';
import { clearUserData } from '@/state/userData';
import { EMPTY_PROGRESS, progressStorageKey, serializeProgress } from '../wizard';
import { useWizardStore } from '../wizardStore';

const tick = () => new Promise((resolve) => setImmediate(resolve));
const wizardKeys = () => Object.keys(secureStore).filter((key) => key.startsWith('couplesync_openfinance_wizard_'));

async function signIn(userId: string, coupleId = 'couple-1') {
  await useSessionStore.getState().setSession(`access-${userId}`, `refresh-${userId}`, userId, coupleId);
}

beforeEach(async () => {
  failWrites = false;
  holdNextWrite = false;
  pendingWrite = null;
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  useWizardStore.getState().reset();
});

describe('progresso do wizard por usuário', () => {
  it('fica gravado na chave do usuário logado e é lido de volta', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();

    expect(await useWizardStore.getState().save({ step: 3, banksConnected: true })).toBe(true);

    expect(wizardKeys()).toEqual([progressStorageKey('user-1')]);
    useWizardStore.getState().reset(); // como se o app tivesse sido fechado
    await useWizardStore.getState().load();
    expect(useWizardStore.getState().loaded).toBe(true);
    expect(useWizardStore.getState().progress).toEqual({ ...EMPTY_PROGRESS, coupleId: 'couple-1', step: 3, banksConnected: true });
  });

  it('sair da conta limpa o progresso: da memória e do armazenamento', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();
    await useWizardStore.getState().save({ step: 4, banksConnected: true, connectionId: 'conn-1' });
    expect(wizardKeys()).toHaveLength(1);

    await clearUserData();

    expect(wizardKeys()).toEqual([]);
    expect(useWizardStore.getState().loaded).toBe(false);
    expect(useWizardStore.getState().userId).toBeNull();
    expect(useWizardStore.getState().progress).toEqual(EMPTY_PROGRESS);
  });

  it('sair da conta limpa também o progresso de uma abertura anterior do app, que nem foi lido nesta', async () => {
    await signIn('user-1');
    secureStore[progressStorageKey('user-1')] = serializeProgress({ ...EMPTY_PROGRESS, coupleId: 'couple-1', step: 3 });

    await clearUserData(); // o wizard não foi aberto nesta sessão: a store não carregou nada

    expect(wizardKeys()).toEqual([]);
  });

  it('outro usuário no mesmo aparelho não vê o progresso do primeiro', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();
    await useWizardStore.getState().save({ step: 3, banksConnected: true });
    // Como se a limpeza de saída não tivesse rodado (o app foi morto): o que ficou é da chave do user-1.
    useWizardStore.getState().reset();

    await signIn('user-2');
    await useWizardStore.getState().load();

    expect(useWizardStore.getState().userId).toBe('user-2');
    expect(useWizardStore.getState().progress).toEqual({ ...EMPTY_PROGRESS, coupleId: 'couple-1' });
    await useWizardStore.getState().save({ step: 2 });
    expect(JSON.parse(secureStore[progressStorageKey('user-1')]).step).toBe(3);
    expect(JSON.parse(secureStore[progressStorageKey('user-2')]).step).toBe(2);
  });

  it('em outro grupo o mesmo usuário começa do zero', async () => {
    await signIn('user-1', 'couple-1');
    await useWizardStore.getState().load();
    await useWizardStore.getState().save({ step: 4, connectionId: 'conn-1' });

    await useSessionStore.getState().setActiveGroup('access-2', 'couple-2');
    useWizardStore.getState().reset();
    await useWizardStore.getState().load();

    expect(useWizardStore.getState().progress).toEqual({ ...EMPTY_PROGRESS, coupleId: 'couple-2' });
  });

  it('o que vai para o armazenamento nunca tem Client ID, Client Secret nem Item ID', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();

    await useWizardStore.getState().save({
      step: 3,
      clientId: 'fake-client-id-0000',
      clientSecret: 'fake-client-secret-0000',
      itemId: 'a1b2c3d4-0000-4000-8000-000000000001',
    } as never);

    const stored = Object.values(secureStore).join('\n');
    expect(stored).not.toContain('fake-client-secret-0000');
    expect(stored).not.toContain('fake-client-id-0000');
    expect(stored).not.toContain('a1b2c3d4');
    expect(Object.keys(JSON.parse(secureStore[progressStorageKey('user-1')])).sort())
      .toEqual(['banksConnected', 'connectionId', 'coupleId', 'step', 'version']);
  });

  it('"concluir" apaga o progresso gravado', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();
    await useWizardStore.getState().save({ step: 4, connectionId: 'conn-1' });

    await useWizardStore.getState().finish();

    expect(wizardKeys()).toEqual([]);
    expect(useWizardStore.getState().progress).toEqual({ ...EMPTY_PROGRESS, coupleId: 'couple-1' });
  });
});

describe('o progresso fica preso à sessão em que foi gravado', () => {
  it('sem sessão nada é lido nem gravado', async () => {
    await useWizardStore.getState().load();
    expect(useWizardStore.getState().loaded).toBe(false);

    expect(await useWizardStore.getState().save({ step: 2 })).toBe(false);
    expect(wizardKeys()).toEqual([]);
  });

  it('uma gravação que termina depois da saída da conta não deixa nada no aparelho', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();
    holdNextWrite = true;

    const saving = useWizardStore.getState().save({ step: 3, banksConnected: true });
    await tick();
    expect(pendingWrite).not.toBeNull();
    await clearUserData();   // saiu da conta com a gravação em andamento
    pendingWrite!.release(); // a gravação termina agora
    expect(await saving).toBe(false);

    expect(wizardKeys()).toEqual([]);
    expect(useWizardStore.getState().progress).toEqual(EMPTY_PROGRESS);
  });

  it('a leitura que termina depois de outro usuário entrar não vale para ele', async () => {
    await signIn('user-1');
    secureStore[progressStorageKey('user-1')] = serializeProgress({ ...EMPTY_PROGRESS, coupleId: 'couple-1', step: 3 });

    const loading = useWizardStore.getState().load();
    await signIn('user-2');
    await loading;

    expect(useWizardStore.getState().userId === 'user-1' && useWizardStore.getState().loaded).toBe(false);
    await useWizardStore.getState().load();
    expect(useWizardStore.getState().userId).toBe('user-2');
    expect(useWizardStore.getState().progress.step).toBe(1);
  });

  it('se não deu para gravar, o wizard segue em memória (só não retoma depois) e o chamador fica sabendo', async () => {
    await signIn('user-1');
    await useWizardStore.getState().load();
    failWrites = true;

    expect(await useWizardStore.getState().save({ step: 3 })).toBe(false);

    expect(useWizardStore.getState().progress.step).toBe(3);
    expect(wizardKeys()).toEqual([]);
  });
});
