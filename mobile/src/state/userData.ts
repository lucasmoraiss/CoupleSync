// Tudo o que pertence ao usuário que está logado e precisa sumir quando ele sai (ou a sessão acaba).
// Cada módulo que guarda dado do usuário em memória registra aqui o seu "limpador"; sair da conta
// roda todos e depois apaga a sessão (tokens no armazenamento seguro). Assim o próximo usuário no
// mesmo aparelho não vê nada do anterior, e um módulo novo só precisa se registrar.
import { useSessionStore } from './sessionStore';

type Cleaner = () => void | Promise<void>;

const cleaners = new Set<Cleaner>();

/** Registra o que limpar ao sair. Chamado uma vez, na carga do módulo que guarda o dado. */
export function registerUserDataCleaner(cleaner: Cleaner): void {
  cleaners.add(cleaner);
}

/** Apaga todos os dados do usuário atual: caches e estados em memória, depois a sessão. */
export async function clearUserData(): Promise<void> {
  for (const cleaner of cleaners) {
    try {
      await cleaner();
    } catch {
      // Um limpador com defeito não pode impedir os outros nem deixar a sessão no aparelho.
    }
  }
  await useSessionStore.getState().clearSession();
}
