// AC-004: Dashboard screen — real API data via TanStack Query
import React from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  ScrollView,
  TouchableOpacity,
  RefreshControl,
} from 'react-native';
import { router } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import { dashboardApiClient } from '@/services/apiClient';
import { useSessionStore } from '@/state/sessionStore';
import { useDashboardStore } from '@/state/dashboardStore';
import { getCategoryLabel, getCategoryIcon } from '@/modules/transactions/categories';
import type { DashboardResponse } from '@/types/api';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { EmptyState } from '@/components/EmptyState';
import { ErrorState } from '@/components/ErrorState';
import { QuickIncomeModal } from '@/components/QuickIncomeModal';

// ─── Design tokens ────────────────────────────────────────────────────────────
const BG = colors.background;
const CARD = colors.surface;
const PRIMARY = colors.primary;
const TEXT = colors.text;
const MUTED = colors.textMuted;
const BORDER = colors.border;
const ERROR = colors.error;

// ─── Helpers ──────────────────────────────────────────────────────────────────
function formatBRL(amount: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(amount);
}

function monthLabel(dateStr: string): string {
  const d = new Date(dateStr);
  return d.toLocaleDateString('pt-BR', { month: 'long', year: 'numeric' });
}

// ─── Sub-components ───────────────────────────────────────────────────────────
function TotalExpensesCard({ data }: { data: DashboardResponse }) {
  return (
    <View style={styles.card}>
      <Text style={styles.cardLabel}>Total de gastos</Text>
      <Text style={styles.cardValue}>{formatBRL(data.totalExpenses)}</Text>
      <Text style={styles.cardHint}>{data.transactionCount} transações</Text>
    </View>
  );
}

function PartnerBreakdownRow({
  data,
  currentUserId,
}: {
  data: DashboardResponse;
  currentUserId: string | null;
}) {
  const sorted = [...data.partnerBreakdown].sort((a, b) => (a.userId === currentUserId ? -1 : 0) - (b.userId === currentUserId ? -1 : 0));
  return (
    <View style={styles.row}>
      {sorted.map((p, i) => (
        <View
          key={p.userId}
          style={[styles.miniCard, { marginLeft: i > 0 ? 8 : 0 }]}
        >
          <Text style={styles.miniLabel}>
            {p.userId === currentUserId ? 'Você' : 'Membro'}
          </Text>
          <Text style={styles.miniValue}>{formatBRL(p.totalAmount)}</Text>
        </View>
      ))}
      {data.partnerBreakdown.length === 0 && (
        <View style={[styles.miniCard, { flex: 1 }]}>
          <Text style={styles.miniLabel}>Sem dados de parceiros</Text>
        </View>
      )}
    </View>
  );
}

function CategoryBreakdown({ data }: { data: DashboardResponse }) {
  const entries = Object.entries(data.expensesByCategory).sort(([, a], [, b]) => b - a);
  if (entries.length === 0) return null;
  return (
    <View style={styles.section}>
      <Text style={styles.sectionTitle}>Por categoria</Text>
      {entries.map(([cat, amount]) => (
        <View key={cat} style={styles.categoryRow}>
          <View style={styles.categoryLeft}>
            <Ionicons name={getCategoryIcon(cat) as any} size={20} color={colors.primaryLight} style={styles.categoryIcon} />
            <Text style={styles.categoryName}>{getCategoryLabel(cat)}</Text>
          </View>
          <Text style={styles.categoryAmount}>{formatBRL(amount)}</Text>
        </View>
      ))}
    </View>
  );
}

// ─── Main screen ──────────────────────────────────────────────────────────────
export default function DashboardScreen() {
  const userId = useSessionStore((s) => s.userId);
  const { startDate, endDate } = useDashboardStore();

  const { data, isLoading, isError, refetch } = useQuery<DashboardResponse>({
    queryKey: ['dashboard', startDate, endDate],
    queryFn: async () => {
      const res = await dashboardApiClient.get({ startDate, endDate });
      return res.data;
    },
  });

  const [refreshing, setRefreshing] = React.useState(false);
  const handleRefresh = async () => {
    setRefreshing(true);
    await refetch();
    setRefreshing(false);
  };

  const [incomeModalVisible, setIncomeModalVisible] = React.useState(false);

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView
        showsVerticalScrollIndicator={false}
        refreshControl={
          <RefreshControl refreshing={refreshing} onRefresh={handleRefresh} tintColor={colors.primaryLight} />
        }
      >
        {/* Header */}
        <View style={styles.header}>
          <View>
            <Text style={styles.greeting}>Dashboard</Text>
            {data ? (
              <Text style={styles.subtitle}>{monthLabel(data.periodStart)}</Text>
            ) : (
              <Text style={styles.subtitle}>Resumo financeiro do casal</Text>
            )}
          </View>
          <TouchableOpacity
            style={styles.transactionsBtn}
            onPress={() => router.push('/transactions' as any)}
            accessibilityLabel="Ver transações"
          >
            <Ionicons name="receipt-outline" size={22} color={colors.primaryLight} />
          </TouchableOpacity>
        </View>

        {/* Loading state */}
        {isLoading && <LoadingState />}

        {/* Error state */}
        {isError && !isLoading && (
          <ErrorState
            message="Não foi possível carregar os dados"
            onRetry={refetch}
          />
        )}

        {/* Data state */}
        {data && !isLoading && (
          <>
            <TotalExpensesCard data={data} />
            <PartnerBreakdownRow data={data} currentUserId={userId} />
            <CategoryBreakdown data={data} />
          </>
        )}

        {/* Empty state — successful response but no data */}
        {!data && !isLoading && !isError && (
          <EmptyState
            icon="bar-chart-outline"
            title="Nenhum dado para o período"
            subtitle="As transações capturadas aparecerão aqui"
            ctaLabel="Ver transações"
            onCtaPress={() => router.push('/transactions' as any)}
          />
        )}

        {/* Quick Income chip */}
        {!isLoading && (
          <TouchableOpacity
            style={styles.incomeChip}
            onPress={() => setIncomeModalVisible(true)}
            accessibilityLabel="Atualizar renda mensal"
          >
            <Ionicons name="wallet-outline" size={18} color={colors.primaryLight} />
            <Text style={styles.incomeChipText}>Atualizar renda</Text>
          </TouchableOpacity>
        )}

        {/* Transactions shortcut */}
        {!isLoading && (
          <TouchableOpacity
            style={styles.viewAllBtn}
            onPress={() => router.push('/transactions' as any)}
            accessibilityLabel="Ver todas as transações"
          >
            <Ionicons name="receipt-outline" size={18} color={PRIMARY} />
            <Text style={styles.viewAllText}>Ver todas as transações</Text>
            <Ionicons name="chevron-forward" size={18} color={PRIMARY} />
          </TouchableOpacity>
        )}
      </ScrollView>

      <QuickIncomeModal
        visible={incomeModalVisible}
        onClose={() => setIncomeModalVisible(false)}
      />
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: BG, paddingHorizontal: 20, paddingTop: 24 },
  header: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 },
  greeting: { fontSize: 22, fontWeight: '700', color: TEXT },
  subtitle: { fontSize: 14, color: MUTED, marginTop: 2 },
  transactionsBtn: { backgroundColor: CARD, borderRadius: 10, padding: 10, borderWidth: 1, borderColor: BORDER },
  centered: { alignItems: 'center', paddingVertical: 48 },
  loadingText: { color: MUTED, marginTop: 12, fontSize: 14 },
  card: {
    backgroundColor: CARD,
    borderRadius: 16,
    padding: 24,
    alignItems: 'center',
    marginBottom: 16,
    borderWidth: 1,
    borderColor: BORDER,
  },
  cardLabel: { fontSize: 13, color: MUTED, marginBottom: 6 },
  cardValue: { fontSize: 34, fontWeight: '800', color: TEXT },
  cardHint: { fontSize: 12, color: MUTED, marginTop: 6 },
  row: { flexDirection: 'row', marginBottom: 16 },
  miniCard: {
    flex: 1,
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 18,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: BORDER,
  },
  miniLabel: { fontSize: 12, color: MUTED, marginBottom: 4 },
  miniValue: { fontSize: 18, fontWeight: '700', color: TEXT },
  section: {
    backgroundColor: CARD,
    borderRadius: 16,
    padding: 16,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: BORDER,
  },
  sectionTitle: {
    fontSize: 12,
    fontWeight: '600',
    color: MUTED,
    marginBottom: 12,
    textTransform: 'uppercase',
    letterSpacing: 0.5,
  },
  categoryRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingVertical: 10,
    borderBottomWidth: 1,
    borderBottomColor: BG,
  },
  categoryLeft: { flexDirection: 'row', alignItems: 'center' },
  categoryIcon: { marginRight: 10 },
  categoryName: { fontSize: 14, color: TEXT },
  categoryAmount: { fontSize: 14, fontWeight: '600', color: TEXT },
  errorCard: {
    backgroundColor: CARD,
    borderRadius: 16,
    padding: 24,
    alignItems: 'center',
    marginBottom: 16,
    borderWidth: 1,
    borderColor: BORDER,
  },
  errorTitle: { color: TEXT, fontSize: 15, fontWeight: '600', marginTop: 12 },
  errorHint: { color: MUTED, fontSize: 13, marginTop: 6, textAlign: 'center' },
  emptyCard: {
    backgroundColor: CARD,
    borderRadius: 16,
    padding: 36,
    alignItems: 'center',
    marginBottom: 16,
    borderWidth: 1,
    borderColor: BORDER,
  },
  emptyText: { color: TEXT, fontSize: 15, fontWeight: '600', marginTop: 16 },
  emptyHint: { color: MUTED, fontSize: 13, marginTop: 6, textAlign: 'center' },
  incomeChip: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    marginBottom: 12,
    borderWidth: 1,
    borderColor: BORDER,
    gap: 8,
  },
  incomeChipText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  viewAllBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 16,
    marginBottom: 24,
    borderWidth: 1,
    borderColor: BORDER,
    gap: 8,
  },
  viewAllText: { color: PRIMARY, fontSize: 14, fontWeight: '600' },
});
