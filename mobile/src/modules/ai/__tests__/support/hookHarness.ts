// Um React mínimo para rodar ganchos fora de uma tela (o Jest daqui não desenha telas: sem react-test-renderer).
// Não é o React: é um MODELO dele, só com o que os ganchos testados usam e com a mesma ordem que importa aqui —
// 1) a função do componente roda; 2) os efeitos daquela passada rodam, na ordem em que foram declarados;
// 3) só então um `setState` feito dentro de um efeito provoca outra passada. É essa ordem que faz o
// `useFocusEffect` do expo-router marcar o foco só na passada seguinte à montagem.

type Cleanup = void | (() => void);

interface EffectSlot {
  deps: ReadonlyArray<unknown> | undefined;
  cleanup: Cleanup;
}

interface Root {
  hooks: unknown[];
  cursor: number;
  pending: Array<{ slot: EffectSlot; effect: () => Cleanup }>;
  dirty: boolean;
  working: boolean;
  unmounted: boolean;
  component: () => void;
}

let current: Root | null = null;

function slotOf<T>(create: () => T): T {
  const root = current;
  if (!root) throw new Error('gancho chamado fora de um componente montado por mount()');
  if (root.cursor === root.hooks.length) root.hooks.push(create());
  return root.hooks[root.cursor++] as T;
}

function sameDeps(a: ReadonlyArray<unknown> | undefined, b: ReadonlyArray<unknown> | undefined): boolean {
  return a !== undefined && b !== undefined && a.length === b.length && a.every((value, i) => Object.is(value, b[i]));
}

function work(root: Root): void {
  if (root.working || root.unmounted) return;
  root.working = true;
  try {
    for (let pass = 0; root.dirty; pass++) {
      if (pass > 50) throw new Error('o componente não para de renderizar');
      root.dirty = false;
      root.cursor = 0;
      root.pending = [];
      current = root;
      try {
        root.component();
      } finally {
        current = null;
      }
      // Como no React: primeiro as limpezas dos efeitos que mudaram, depois os efeitos, na ordem de declaração.
      const pending = root.pending;
      for (const { slot } of pending) {
        if (typeof slot.cleanup === 'function') slot.cleanup();
        slot.cleanup = undefined;
      }
      for (const { slot, effect } of pending) slot.cleanup = effect();
    }
  } finally {
    root.working = false;
  }
}

function schedule(root: Root): void {
  root.dirty = true;
  // Dentro de uma passada, a próxima só começa quando os efeitos desta terminarem; fora dela, roda na hora.
  work(root);
}

function useRef<T>(initial: T): { current: T } {
  return slotOf(() => ({ current: initial }));
}

function useMemo<T>(create: () => T, deps: ReadonlyArray<unknown>): T {
  const slot = slotOf<{ deps: ReadonlyArray<unknown> | undefined; value: T | undefined }>(() => ({ deps: undefined, value: undefined }));
  if (!sameDeps(slot.deps, deps)) {
    slot.value = create();
    slot.deps = deps;
  }
  return slot.value as T;
}

function useCallback<T>(fn: T, deps: ReadonlyArray<unknown>): T {
  return useMemo(() => fn, deps);
}

function useState<T>(initial: T | (() => T)): [T, (next: T | ((previous: T) => T)) => void] {
  const root = current as Root;
  const slot = slotOf(() => {
    const state = { value: typeof initial === 'function' ? (initial as () => T)() : initial, set: (_next: T | ((previous: T) => T)) => {} };
    state.set = (next) => {
      const value = typeof next === 'function' ? (next as (previous: T) => T)(state.value) : next;
      if (Object.is(value, state.value)) return;
      state.value = value;
      schedule(root);
    };
    return state;
  });
  return [slot.value, slot.set];
}

function useEffect(effect: () => Cleanup, deps?: ReadonlyArray<unknown>): void {
  const root = current as Root;
  const slot = slotOf<EffectSlot & { first: boolean }>(() => ({ deps: undefined, cleanup: undefined, first: true }));
  if (slot.first || !sameDeps(slot.deps, deps)) root.pending.push({ slot, effect });
  slot.first = false;
  slot.deps = deps;
}

function useSyncExternalStore<T>(subscribe: (listener: () => void) => () => void, getSnapshot: () => T): T {
  const root = current as Root;
  const slot = slotOf(() => ({ unsubscribe: subscribe(() => schedule(root)) }));
  void slot;
  return getSnapshot();
}

/** O que vai no lugar do módulo `react` (jest.mock). */
export const react = {
  useRef,
  useMemo,
  useCallback,
  useState,
  useEffect,
  useLayoutEffect: useEffect,
  useSyncExternalStore,
  useDebugValue: () => undefined,
};

export interface Mounted {
  /** Renderiza de novo (o pai passou outras propriedades: o componente lê o que mudou do lado de fora). */
  rerender(): void;
  unmount(): void;
}

/** Monta um componente sem tela: roda a função, os efeitos dela e as passadas que eles provocarem. */
export function mount(component: () => void): Mounted {
  const root: Root = { hooks: [], cursor: 0, pending: [], dirty: true, working: false, unmounted: false, component };
  work(root);
  return {
    rerender: () => schedule(root),
    unmount: () => {
      for (const hook of root.hooks) {
        const slot = hook as Partial<EffectSlot> & { unsubscribe?: () => void };
        if (typeof slot?.cleanup === 'function') slot.cleanup();
        if (typeof slot?.unsubscribe === 'function') slot.unsubscribe();
      }
      root.unmounted = true;
    },
  };
}

/** A navegação de mentira que o `useFocusEffect` de verdade consulta e escuta. */
export function fakeNavigation(focused: boolean) {
  const listeners: Record<string, Array<() => void>> = { focus: [], blur: [] };
  let isFocused = focused;
  return {
    isFocused: () => isFocused,
    addListener: (event: string, listener: () => void) => {
      (listeners[event] ??= []).push(listener);
      return () => {
        listeners[event] = listeners[event].filter((item) => item !== listener);
      };
    },
    /** A tela recebe ou perde o foco (a pessoa navegou). */
    setFocused(next: boolean) {
      if (next === isFocused) return;
      isFocused = next;
      [...listeners[next ? 'focus' : 'blur']].forEach((listener) => listener());
    },
  };
}
