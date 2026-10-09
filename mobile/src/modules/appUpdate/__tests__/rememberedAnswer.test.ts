// Issue #3 (B7): o aparelho LEMBRA a última resposta do servidor sobre versões. Quem já se soube bloqueado abre
// direto no bloqueio, sem esperar a rede; a comparação é sempre com a versão instalada AGORA.
const secureStore: Record<string, string> = {};
let failReads = false;
let failWrites = false;
let hangReads = false;

jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    if (failWrites) throw new Error('keystore indisponível');
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn((key: string) => {
    if (hangReads) return new Promise<string | null>(() => undefined);
    if (failReads) return Promise.reject(new Error('keystore indisponível'));
    return Promise.resolve(secureStore[key] ?? null);
  }),
  deleteItemAsync: jest.fn(async (key: string) => {
    delete secureStore[key];
  }),
}));

import * as SecureStore from 'expo-secure-store';
import { useSessionStore } from '@/state/sessionStore';
import { clearUserData } from '@/state/userData';
import { blocksApp, parseRememberedAnswer, resolveUpdate, serializeAnswer, showDashboardNotice } from '../appUpdate';
import { MEMORY_LOAD_TIMEOUT_MS, REMEMBERED_ANSWER_KEY, useRememberedAnswer } from '../rememberedAnswerStore';

const store = () => useRememberedAnswer.getState();
const answer = (latestVersion: string | null, minimumVersion: string | null) => ({ latestVersion, minimumVersion });
const loaded = (remembered: ReturnType<typeof answer> | null) => ({ loaded: true, answer: remembered });
const NOT_LOADED = { loaded: false, answer: null };

beforeEach(() => {
  failReads = false;
  failWrites = false;
  hangReads = false;
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  store().resetForTests();
  jest.clearAllMocks();
});

describe('o que decide a abertura do app (regra pura)', () => {
  it('aparelho que já se soube bloqueado abre direto no bloqueio, sem resposta da rede', () => {
    const update = resolveUpdate({ installed: '1.0.0', live: undefined, memory: loaded(answer('1.2.0', '1.1.0')) });

    expect(blocksApp(update)).toBe(true);
    expect(update.waitingForMemory).toBe(false);
  });

  it('a comparação usa a versão instalada ATUAL: quem atualizou o APK destrava na hora, com a lembrança antiga', () => {
    const update = resolveUpdate({ installed: '1.2.0', live: undefined, memory: loaded(answer('1.2.0', '1.1.0')) });

    expect(blocksApp(update)).toBe(false);
    expect(update.decision).toBe('nenhum');
  });

  it('sem lembrança (primeira abertura, ou ilegível): abre normal', () => {
    const update = resolveUpdate({ installed: '1.0.0', live: undefined, memory: loaded(null) });

    expect(blocksApp(update)).toBe(false);
    expect(update.waitingForMemory).toBe(false);
    expect(update.decision).toBe('nenhum');
  });

  it('enquanto a lembrança não foi lida do aparelho o layout espera por ela (leitura local), não pela rede', () => {
    const update = resolveUpdate({ installed: '1.0.0', live: undefined, memory: NOT_LOADED });

    expect(update.waitingForMemory).toBe(true);
    expect(blocksApp(update)).toBe(false);
  });

  it('com a resposta do servidor em mãos ninguém espera a lembrança', () => {
    const update = resolveUpdate({ installed: '1.0.0', live: answer('1.1.0', null), memory: NOT_LOADED });

    expect(update.waitingForMemory).toBe(false);
    expect(update.decision).toBe('aviso');
  });

  it('versão instalada desconhecida: nada a esperar, nada a bloquear, mesmo com lembrança de bloqueio', () => {
    expect(resolveUpdate({ installed: null, live: undefined, memory: NOT_LOADED }).waitingForMemory).toBe(false);
    const update = resolveUpdate({ installed: null, live: undefined, memory: loaded(answer('9.9.9', '9.9.9')) });
    expect(blocksApp(update)).toBe(false);
  });

  it('a resposta nova do servidor vale mais que a lembrança: destrava quando a mínima foi corrigida', () => {
    const memory = loaded(answer('1.2.0', '1.1.0'));

    expect(blocksApp(resolveUpdate({ installed: '1.0.0', live: answer('1.2.0', null), memory }))).toBe(false);
    expect(blocksApp(resolveUpdate({ installed: '1.0.0', live: answer(null, null), memory }))).toBe(false);
    expect(blocksApp(resolveUpdate({ installed: '1.0.0', live: {}, memory }))).toBe(false);
  });

  it('a resposta nova também bloqueia quem a lembrança dizia liberado (descoberto com o app aberto)', () => {
    const update = resolveUpdate({ installed: '1.0.0', live: answer('1.2.0', '1.1.0'), memory: loaded(answer('1.2.0', null)) });

    expect(blocksApp(update)).toBe(true);
  });

  it('a lembrança também mostra o aviso do Painel sem esperar a rede (API dormindo)', () => {
    const update = resolveUpdate({ installed: '1.0.0', live: undefined, memory: loaded(answer('1.1.0', null)) });

    expect(showDashboardNotice(update, { loaded: true, dismissedVersion: null })).toBe(true);
  });
});

describe('o que é guardado', () => {
  it('só as duas versões, já reduzidas a X.Y.Z; o link de download não é guardado', () => {
    const text = serializeAnswer({ latestVersion: 'v1.2.0', minimumVersion: '1.1.0-pit', downloadUrl: 'https://outro.example/x.apk', extra: 1 });

    expect(JSON.parse(text!)).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
  });

  it('campos desconhecidos viram null; o que não é uma resposta não é guardado', () => {
    expect(JSON.parse(serializeAnswer({ latestVersion: 'nova', minimumVersion: 42 })!)).toEqual({ latestVersion: null, minimumVersion: null });
    expect(serializeAnswer(undefined)).toBeNull();
    expect(serializeAnswer(null)).toBeNull();
    expect(serializeAnswer('<html>erro</html>')).toBeNull();
    expect(serializeAnswer([])).toBeNull();
  });

  it.each([
    ['nada guardado', null],
    ['texto vazio', ''],
    ['não é JSON', '{"latestVersion":'],
    ['lista', '[]'],
    ['número', '42'],
    ['texto JSON', '"1.1.0"'],
    ['null', 'null'],
  ])('lembrança ilegível (%s) → nenhuma lembrança, sem exceção', (_name, text) => {
    expect(parseRememberedAnswer(text)).toBeNull();
  });

  it('lembrança com campos que não são versão → campos null (não bloqueia)', () => {
    const remembered = parseRememberedAnswer('{"latestVersion":"1.2.0","minimumVersion":{"x":1}}');

    expect(remembered).toEqual({ latestVersion: '1.2.0', minimumVersion: null });
    expect(blocksApp(resolveUpdate({ installed: '1.0.0', live: undefined, memory: loaded(remembered) }))).toBe(false);
  });
});

describe('a lembrança no aparelho', () => {
  it('a resposta do servidor é gravada e lida na abertura seguinte', async () => {
    await store().load();
    expect(store()).toMatchObject({ loaded: true, answer: null });

    await store().remember({ latestVersion: '1.2.0', minimumVersion: '1.1.0', downloadUrl: 'x' });
    expect(JSON.parse(secureStore[REMEMBERED_ANSWER_KEY])).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });

    store().resetForTests(); // o app foi fechado e aberto
    expect(store().loaded).toBe(false);
    await store().load();
    expect(store().answer).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
    expect(blocksApp(resolveUpdate({ installed: '1.0.0', live: undefined, memory: store() }))).toBe(true);
  });

  it('a mesma resposta não é gravada de novo; uma diferente substitui', async () => {
    await store().load();
    await store().remember({ latestVersion: '1.2.0', minimumVersion: null });
    await store().remember({ latestVersion: 'v1.2.0', minimumVersion: null, downloadUrl: 'outro' });
    expect(SecureStore.setItemAsync).toHaveBeenCalledTimes(1);

    await store().remember({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
    expect(SecureStore.setItemAsync).toHaveBeenCalledTimes(2);
    expect(store().answer).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
  });

  it('o que não é uma resposta do servidor não apaga a lembrança', async () => {
    await store().load();
    await store().remember({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
    await store().remember(undefined);
    await store().remember('<html>erro</html>');

    expect(store().answer).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
  });

  it('armazenamento que falha ao ler: abre normal (sem lembrança)', async () => {
    secureStore[REMEMBERED_ANSWER_KEY] = '{"latestVersion":"1.2.0","minimumVersion":"1.1.0"}';
    failReads = true;

    await store().load();

    expect(store()).toMatchObject({ loaded: true, answer: null });
  });

  it('conteúdo ilegível no armazenamento: abre normal', async () => {
    secureStore[REMEMBERED_ANSWER_KEY] = 'isto não é JSON';

    await store().load();

    expect(store()).toMatchObject({ loaded: true, answer: null });
  });

  it('armazenamento que não responde: a espera tem fim e o app abre normal', async () => {
    jest.useFakeTimers();
    try {
      hangReads = true;
      const loading = store().load();
      expect(store().loaded).toBe(false);

      jest.advanceTimersByTime(MEMORY_LOAD_TIMEOUT_MS);
      await loading;

      expect(store()).toMatchObject({ loaded: true, answer: null });
      expect(MEMORY_LOAD_TIMEOUT_MS).toBeLessThanOrEqual(3000);
    } finally {
      jest.useRealTimers();
    }
  });

  it('falha ao gravar: a resposta vale enquanto o app está aberto, sem exceção', async () => {
    await store().load();
    failWrites = true;

    await store().remember({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });

    expect(store().answer).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
  });

  it('uma resposta que chega enquanto a leitura corre é mais nova do que o que estava gravado', async () => {
    secureStore[REMEMBERED_ANSWER_KEY] = '{"latestVersion":"1.2.0","minimumVersion":"1.1.0"}';
    const loading = store().load();
    await store().remember({ latestVersion: '1.2.0', minimumVersion: null });
    await loading;

    expect(store().answer).toEqual({ latestVersion: '1.2.0', minimumVersion: null });
  });

  it('é por aparelho: sair da conta não apaga a lembrança', async () => {
    await store().load();
    await store().remember({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
    await useSessionStore.getState().setSession('access-1', 'refresh-1', 'user-1', 'couple-1');

    await clearUserData();

    expect(useSessionStore.getState().accessToken).toBeNull();
    expect(store().answer).toEqual({ latestVersion: '1.2.0', minimumVersion: '1.1.0' });
    expect(secureStore[REMEMBERED_ANSWER_KEY]).toBeDefined();
  });
});
