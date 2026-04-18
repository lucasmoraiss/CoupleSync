// AC-004: Client-side dashboard store — holds selected period range (Zustand, client state only)
import { create } from 'zustand';

function formatDate(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

function currentMonthRange(): { startDate: string; endDate: string } {
  const now = new Date();
  const start = new Date(now.getFullYear(), now.getMonth(), 1);
  return { startDate: formatDate(start), endDate: formatDate(now) };
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
