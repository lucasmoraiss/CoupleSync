// Tudo o que pertence ao usuário que está logado e precisa sumir quando ele sai (ou a sessão acaba).
// Cada módulo que guarda dado do usuário em memória registra aqui o seu "limpador"; sair da conta
// roda todos e apaga a sessão (tokens no armazenamento seguro). Assim o próximo usuário no
// mesmo aparelho não vê nada do anterior, e um módulo novo só precisa se registrar.
import { useSessionStore } from './sessionStore';

type Cleaner = () => void | Promise<void>;

const cleaners = new Set<Cleaner>();

/** Registra o que limpar ao sair. Chamado uma vez, na carga do módulo que guarda o dado. */
export function registerUserDataCleaner(cleaner: Cleaner): void {
  cleaners.add(cleaner);
}

/** Roda todos os limpadores (caches e estados em memória), sem mexer na sessão. Usado também ao entrar numa conta. */
export async function resetUserCaches(): Promise<void> {
  for (const cleaner of cleaners) {
    try {
      await cleaner();
    } catch {
      // Um limpador com defeito não pode impedir os outros.
    }
  }
}

/**
 * Apaga todos os dados do usuário atual. Ordem importa: a sessão em memória é zerada ANTES de qualquer
 * limpador (as telas ainda montadas reagem à limpeza refazendo consultas; sem token elas não buscam nada
 * do usuário que saiu); só então os caches são cancelados e esvaziados; por fim espera-se o armazenamento seguro.
 */
export async function clearUserData(): Promise<void> {
  const sessionCleared = useSessionStore.getState().clearSession(); // parte síncrona já zerou a memória
  try {
    await resetUserCaches();
  } finally {
    await sessionCleared.catch(() => undefined);
  }
}
