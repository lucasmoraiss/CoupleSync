// Sobe a cada troca de grupo ativo. O layout principal usa o valor como `key` das telas: quando muda, todas são
// remontadas e buscam os dados do grupo novo num cache já vazio (nenhuma tela escondida fica com o que era do
// grupo anterior). Vale só em memória.
import { create } from 'zustand';

interface GroupEpochStore {
  epoch: number;
  bump: () => void;
}

export const useGroupEpoch = create<GroupEpochStore>((set) => ({
  epoch: 0,
  bump: () => set((state) => ({ epoch: state.epoch + 1 })),
}));
