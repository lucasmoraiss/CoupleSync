// AC-006: CashFlow screen — 30-day and 90-day horizon projections
import React, { useState } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  TouchableOpacity,
  ScrollView,
} from 'react-native';
import { useQuery } from '@tanstack/react-query';
import { cashFlowApiClient } from '@/services/apiClient';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { EmptyState } from '@/components/EmptyState';
import { ErrorState } from '@/components/ErrorState';

type Horizon = 30 | 90;

const formatBRL = (value: number): string =>
  Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(value);

const formatDatePtBR = (iso: string): string =>
  new Date(iso).toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' });

export default function CashFlowScreen() {
  const [horizon, setHorizon] = useState<Horizon>(30);

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['cashflow', horizon],
    queryFn: () => cashFlowApiClient.get(horizon).then((r) => r.data),
    staleTime: 60_000,
  });

  return (
    <SafeAreaView style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.title}>Fluxo de Caixa</Text>
        <Text style={styles.subtitle}>Projeção de despesas</Text>
      </View>

      {/* Horizon toggle */}
      <View style={styles.tabRow}>
        {([30, 90] as Horizon[]).map((h) => (
          <TouchableOpacity
            key={h}
            style={[styles.tab, horizon === h && styles.tabActive]}
            onPress={() => setHorizon(h)}
            accessibilityRole="tab"
            accessibilityState={{ selected: horizon === h }}
          >
            <Text style={[styles.tabText, horizon === h && styles.tabTextActive]}>
              {h} dias
            </Text>
          </TouchableOpacity>
        ))}
      </View>

      {isLoading && <LoadingState message="Calculando projeção..." />}

      {isError && (
        <ErrorState
          message="Não foi possível carregar os dados."
          onRetry={() => refetch()}
        />
      )}

      {!isLoading && !isError && !data && (
        <EmptyState
          icon="stats-chart-outline"
          title="Sem dados ainda"
          subtitle="O fluxo aparecerá quando houver transações registradas"
        />
      )}

      {!isLoading && !isError && data && (
        <ScrollView
          showsVerticalScrollIndicator={false}
          contentContainerStyle={styles.scrollContent}
        >
          {/* Main summary card */}
          <View style={styles.card}>
            <Text style={styles.cardLabel}>Gasto histórico ({horizon} dias)</Text>
            <Text style={styles.cardValue}>{formatBRL(data.totalHistoricalSpend)}</Text>
            <Text style={styles.periodText}>
              {formatDatePtBR(data.historicalPeriodStart)} –{' '}
              {formatDatePtBR(data.historicalPeriodEnd)} · {data.transactionCount} transações
            </Text>
          </View>

          {/* Averages row */}
          <View style={styles.cardRow}>
            <View style={[styles.card, styles.cardHalf]}>
              <Text style={styles.cardLabel}>Média diária</Text>
              <Text style={[styles.cardValue, styles.cardValueSm]}>
                {formatBRL(data.averageDailySpend)}
              </Text>
            </View>
            <View style={[styles.card, styles.cardHalf]}>
              <Text style={styles.cardLabel}>Projeção {horizon}d</Text>
              <Text style={[styles.cardValue, styles.cardValueSm, styles.projectedValue]}>
                {formatBRL(data.projectedSpend)}
              </Text>
            </View>
          </View>

          {/* Category breakdown */}
          {Object.keys(data.categoryBreakdown).length > 0 && (
            <View style={styles.section}>
              <Text style={styles.sectionTitle}>Por categoria</Text>
              {Object.entries(data.categoryBreakdown)
                .sort(([, a], [, b]) => b - a)
                .map(([category, amount]) => (
                  <View key={category} style={styles.categoryRow}>
                    <Text style={styles.categoryName}>{category}</Text>
                    <Text style={styles.categoryAmount}>{formatBRL(amount)}</Text>
                  </View>
                ))}
            </View>
          )}

          {/* Assumptions */}
          {!!data.assumptions && (
            <View style={styles.assumptionsCard}>
              <Text style={styles.assumptionsLabel}>Premissas do cálculo</Text>
              <Text style={styles.assumptionsText}>{data.assumptions}</Text>
            </View>
          )}
        </ScrollView>
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background, paddingHorizontal: 20, paddingTop: 24 },
  header: { marginBottom: 20 },
  title: { fontSize: 26, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, marginTop: 4 },
  tabRow: {
    flexDirection: 'row',
    backgroundColor: colors.surface,
    borderRadius: 12,
    padding: 4,
    marginBottom: 20,
  },
  tab: { flex: 1, paddingVertical: 10, borderRadius: 10, alignItems: 'center' },
  tabActive: { backgroundColor: colors.primary },
  tabText: { fontSize: 14, fontWeight: '600', color: colors.textMuted },
  tabTextActive: { color: colors.text },
  centered: { flex: 1, justifyContent: 'center', alignItems: 'center', gap: 12 },
  muted: { fontSize: 14, color: colors.textMuted },
  errorText: { fontSize: 16, color: colors.errorLight, fontWeight: '600' },
  retryButton: {
    backgroundColor: colors.primary,
    paddingHorizontal: 24,
    paddingVertical: 12,
    borderRadius: 10,
  },
  retryText: { color: colors.text, fontWeight: '600' },
  emptyEmoji: { fontSize: 56 },
  emptyTitle: { fontSize: 20, fontWeight: '700', color: colors.text },
  scrollContent: { paddingBottom: 32 },
  card: { backgroundColor: colors.surface, borderRadius: 16, padding: 20, marginBottom: 12 },
  cardRow: { flexDirection: 'row', gap: 12 },
  cardHalf: { flex: 1, marginBottom: 12 },
  cardLabel: { fontSize: 13, color: colors.textMuted, marginBottom: 6 },
  cardValue: { fontSize: 26, fontWeight: '700', color: colors.text },
  cardValueSm: { fontSize: 20 },
  projectedValue: { color: colors.errorLight },
  periodText: { fontSize: 12, color: colors.textDisabled, marginTop: 6 },
  section: { backgroundColor: colors.surface, borderRadius: 16, padding: 20, marginBottom: 12 },
  sectionTitle: { fontSize: 16, fontWeight: '700', color: colors.text, marginBottom: 14 },
  categoryRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    paddingVertical: 10,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
  },
  categoryName: { fontSize: 14, color: colors.textSubtle, textTransform: 'capitalize' },
  categoryAmount: { fontSize: 14, fontWeight: '600', color: colors.text },
  assumptionsCard: {
    backgroundColor: colors.surface,
    borderRadius: 16,
    padding: 20,
    borderLeftWidth: 3,
    borderLeftColor: colors.primary,
    marginBottom: 12,
  },
  assumptionsLabel: { fontSize: 13, color: colors.primaryLight, fontWeight: '600', marginBottom: 6 },
  assumptionsText: { fontSize: 14, color: colors.textMuted, lineHeight: 20 },
});
