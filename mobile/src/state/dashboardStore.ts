// AC-004: Client-side dashboard store — holds selected period range (Zustand, client state only)
import { create } from 'zustand';

// Vazio = mês corrente. Quem decide qual é o mês corrente é o servidor (fuso America/Sao_Paulo);
// o aparelho não calcula o período, para os dois nunca discordarem.
function currentMonthRange(): { startDate: string; endDate: string } {
  return { startDate: '', endDate: '' };
}

interface DashboardStore {
  startDate: string;
  endDate: string;
  setDateRange: (startDate: string, endDate: string) => void;
  resetToCurrentMonth: () => void;
}

export const useDashboardStore = create<DashboardStore>((set) => {
  const { startDate, endDate } = currentMonthRange();
  return {
    startDate,
    endDate,
    setDateRange: (start, end) => set({ startDate: start, endDate: end }),
    resetToCurrentMonth: () => set(currentMonthRange()),
  };
});
