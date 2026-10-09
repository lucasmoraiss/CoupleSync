// Issue #60, revisão final 1 (I1): a tela de boas-vindas da IA, MONTADA JÁ EM FOCO sem nada a perguntar, tem de
// sair para o Painel (caminho real: ativar pela boas-vindas, cair no Painel, voltar do Android — o histórico das
// abas devolve o foco à boas-vindas, remontada com "a pessoa já ativou"). E continua valendo o item 6: a instância
// escondida não tira ninguém de onde está.
//
// O que é de verdade aqui e o que é modelo:
// - de verdade: o `useFocusEffect` do expo-router instalado (build/useFocusEffect.js) e o `useOptionalNavigation`
//   dele (build/link/useLoadedNavigation.js) — é dali que vem "o foco só é marcado na passada seguinte à montagem";
//   o gancho da tela (useLeaveWelcome.ts), a regra (leaveWelcomeIfDue), useAiStatus.ts e o store do status;
// - modelo: o React (support/hookHarness.ts: função, depois os efeitos na ordem, depois a passada que eles pedem)
//   e a navegação (um objeto com isFocused/addListener). O Jest daqui não monta telas: a tela em si
//   (welcome.tsx) é conferida por leitura de código, no fim.
import * as fs from 'fs';
import * as path from 'path';
import { fakeNavigation, mount } from './support/hookHarness';

jest.mock('react', () => jest.requireActual('./support/hookHarness').react);

let mockNavigation: ReturnType<typeof fakeNavigation>;
jest.mock('@react-navigation/native', () => ({ useNavigation: () => mockNavigation }));
// O estado da navegação já carregou (o app está aberto): o expo-router entrega a navegação assim que pode.
jest.mock('expo-router/build/global-state/router-store', () => ({ useExpoRouter: () => ({ navigationRef: { current: {} } }) }));
jest.mock('expo-router', () => ({
  useFocusEffect: jest.requireActual('expo-router/build/useFocusEffect').useFocusEffect,
}));

const secureStore: Record<string, string> = {};
jest.mock('expo-secure-store', () => ({
  setItemAsync: jest.fn(async (key: string, value: string) => {
    secureStore[key] = value;
  }),
  getItemAsync: jest.fn(async (key: string) => secureStore[key] ?? null),
  deleteItemAsync: jest.fn(async (key: string) => {
    delete secureStore[key];
  }),
}));

const mockAiStatus = jest.fn();
jest.mock('@/services/apiClient', () => ({
  aiApiClient: { getStatus: (...args: unknown[]) => mockAiStatus(...args) },
}));

import { useEffect } from 'react';
import { leaveWelcomeIfDue } from '../aiStatus';
import { useAiStatus } from '../useAiStatus';
import { useLeaveWelcomeWhenDue } from '../useLeaveWelcome';
import { clearUserData } from '@/state/userData';

async function settle() {
  for (let i = 0; i < 20; i++) await Promise.resolve();
}

beforeEach(async () => {
  await clearUserData();
  for (const key of Object.keys(secureStore)) delete secureStore[key];
  mockAiStatus.mockReset().mockRejectedValue(new Error('sem servidor'));
});

afterEach(async () => {
  await clearUserData();
});

/** A tela, só com o que decide a saída: o que ela recebe pode mudar entre uma passada e outra. */
function welcomeScreen(initial: { nothingToAsk: boolean; busy: boolean }, focused: boolean) {
  mockNavigation = fakeNavigation(focused);
  const navigation = mockNavigation;
  const props = { ...initial };
  const leave = jest.fn();
  const mounted = mount(() => useLeaveWelcomeWhenDue(props.nothingToAsk, props.busy, leave));
  return {
    leave,
    navigation,
    set(next: Partial<typeof props>) {
      Object.assign(props, next);
      mounted.rerender();
    },
    unmount: mounted.unmount,
  };
}

describe('boas-vindas da IA: sair sozinha para o Painel, com o foco como o expo-router o entrega', () => {
  it('montada JÁ EM FOCO e sem nada a perguntar (voltar do Android depois de ativar): sai para o Painel', () => {
    const screen = welcomeScreen({ nothingToAsk: true, busy: false }, true);
    expect(screen.leave).toHaveBeenCalledTimes(1);
  });

  it('montada em foco com a pergunta a fazer: fica; quando deixa de ter o que perguntar (ativaram em outro aparelho), sai', () => {
    const screen = welcomeScreen({ nothingToAsk: false, busy: false }, true);
    expect(screen.leave).not.toHaveBeenCalled();

    screen.set({ nothingToAsk: true });
    expect(screen.leave).toHaveBeenCalledTimes(1);
  });

  it('gravando a resposta: não sai; a gravação termina e não há mais o que perguntar: sai', () => {
    const screen = welcomeScreen({ nothingToAsk: true, busy: true }, true);
    expect(screen.leave).not.toHaveBeenCalled();

    screen.set({ busy: false });
    expect(screen.leave).toHaveBeenCalledTimes(1);
  });

  it('item 6 — ESCONDIDA (a pessoa está em outra tela): não navega, nem na montagem nem quando o status muda', () => {
    const hidden = welcomeScreen({ nothingToAsk: true, busy: false }, false);
    expect(hidden.leave).not.toHaveBeenCalled();

    const asking = welcomeScreen({ nothingToAsk: false, busy: false }, false);
    asking.set({ nothingToAsk: true });
    expect(asking.leave).not.toHaveBeenCalled();
  });

  it('em foco, perde o foco e SÓ ENTÃO o status muda: não navega; ao voltar a ter foco, sai', () => {
    const screen = welcomeScreen({ nothingToAsk: false, busy: false }, true);
    screen.navigation.setFocused(false);
    screen.set({ nothingToAsk: true });
    expect(screen.leave).not.toHaveBeenCalled();

    screen.navigation.setFocused(true);
    expect(screen.leave).toHaveBeenCalledTimes(1);
  });

  it('sai uma vez só por motivo: outra passada sem mudança nenhuma não navega de novo', () => {
    const screen = welcomeScreen({ nothingToAsk: true, busy: false }, true);
    screen.set({});
    screen.set({});
    expect(screen.leave).toHaveBeenCalledTimes(1);
  });

  // A causa do defeito, executada (não só lida): é por isto que a regra não pode ficar num useEffect que pergunta
  // o foco ao useAiStatus. Se um dia esta afirmação falhar (a biblioteca passou a marcar o foco na montagem), o
  // gancho useLeaveWelcomeWhenDue continua certo — é só este teste que perde o sentido.
  it('a ligação ANTIGA (useEffect + isFocused() de useAiStatus) não saía: na montagem o foco ainda não foi marcado', async () => {
    mockNavigation = fakeNavigation(true);
    const leave = jest.fn();
    const focusSeenByTheEffect: boolean[] = [];
    let isFocusedNow: () => boolean = () => false;
    const mounted = mount(() => {
      const { isFocused } = useAiStatus();
      isFocusedNow = isFocused;
      useEffect(() => {
        focusSeenByTheEffect.push(isFocused());
        leaveWelcomeIfDue(true, false, { isFocused, leave });
      }, [isFocused]);
    });
    await settle();

    // O efeito rodou uma vez, na montagem, e viu "fora de foco"; depois o foco foi marcado e ninguém o chamou de novo.
    expect(focusSeenByTheEffect).toEqual([false]);
    expect(isFocusedNow()).toBe(true);
    expect(leave).not.toHaveBeenCalled();
    mounted.unmount();
  });
});

describe('a tela usa o gancho', () => {
  const source = fs.readFileSync(path.resolve(__dirname, '../../../../app/(main)/ai/welcome.tsx'), 'utf8');

  it('welcome.tsx entrega a saída ao gancho de foco, com a volta ao Painel', () => {
    expect(source).toMatch(/useLeaveWelcomeWhenDue\(leave, busy !== null, \(\) => goToParent\('ai\/welcome'\)\);/);
    // Nenhum efeito da tela decide a saída perguntando o foco a outro gancho.
    expect(source).not.toMatch(/leaveWelcomeIfDue\(/);
    expect(source).not.toMatch(/if \(leave && busy === null\) goToParent/);
  });
});
