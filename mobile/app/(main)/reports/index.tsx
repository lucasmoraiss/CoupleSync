// AC-614: Reports screen — spending by category (pie) + monthly trends (bar)
import React, { useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useQuery } from '@tanstack/react-query';
import { PieChart, BarChart } from 'react-native-gifted-charts';
import { reportsApiClient } from '@/services/apiClient';
import { colors, spacing, typography, borderRadius } from '@/theme';
import { ErrorBoundary } from '@/components/ErrorBoundary';

const PERIOD_OPTIONS = [
  { label: '3m', months: 3 },
  { label: '6m', months: 6 },
  { label: '12m', months: 12 },
] as const;

type Period = (typeof PERIOD_OPTIONS)[number]['months'];

function formatBRL(value: number): string {
  return value.toLocaleString('pt-BR', { style: 'currency', currency: 'BRL' });
}

export default function ReportsScreen() {
  return (
    <ErrorBoundary fallbackTitle="Erro ao carregar Relatórios">
      <ReportsScreenInner />
    </ErrorBoundary>
  );
}

function ReportsScreenInner() {
  const [period, setPeriod] = useState<Period>(6);

  const {
    data: spendingData,
    isLoading: spendingLoading,
    isError: spendingError,
    refetch: refetchSpending,
  } = useQuery({
    queryKey: ['reports', 'spending-by-category', period],
    queryFn: () => reportsApiClient.spendingByCategory(period).then((r) => r.data),
    retry: 1,
  });

  const {
    data: trendsData,
    isLoading: trendsLoading,
    isError: trendsError,
    refetch: refetchTrends,
  } = useQuery({
    queryKey: ['reports', 'monthly-trends', period],
    queryFn: () => reportsApiClient.monthlyTrends(period).then((r) => r.data),
    retry: 1,
  });

  const isLoading = spendingLoading || trendsLoading;
  const hasError = spendingError || trendsError;

  const pieData =
    spendingData?.categories
      // Defensive: gifted-charts crashes when `value` is 0, negative or `color` is missing.
      ?.filter((c) => typeof c.total === 'number' && c.total > 0 && typeof c.color === 'string' && c.color.length > 0)
      .map((c) => ({
        value: c.total,
        color: c.color,
        text: `${(c.percentage ?? 0).toFixed(0)}%`,
        label: c.name,
        focused: false,
      })) ?? [];

  const barData =
    trendsData?.months
      ?.filter((m) => typeof m.expense === 'number' && !Number.isNaN(m.expense))
      .map((m) => ({
        value: Math.max(0, m.expense),
        label: (m.month ?? '').slice(5), // "MM" portion
        frontColor: colors.primary,
        topLabelComponent: () => null,
      })) ?? [];

  const hasSpendingData = pieData.length > 0 && pieData.some((d) => d.value > 0);
  const hasTrendsData = barData.length > 0 && barData.some((d) => d.value > 0);

  return (
    <SafeAreaView style={styles.container} edges={['top']}>
      <ScrollView contentContainerStyle={styles.scroll} showsVerticalScrollIndicator={false}>
        {/* Header */}
        <Text style={styles.screenTitle}>Relatórios</Text>

        {/* Period selector */}
        <View style={styles.periodRow}>
          {PERIOD_OPTIONS.map((opt) => (
            <Pressable
              key={opt.months}
              style={[styles.periodBtn, period === opt.months && styles.periodBtnActive]}
              onPress={() => setPeriod(opt.months)}
              accessibilityRole="button"
              accessibilityState={{ selected: period === opt.months }}
            >
              <Text
                style={[
                  styles.periodBtnText,
                  period === opt.months && styles.periodBtnTextActive,
                ]}
              >
                {opt.label}
              </Text>
            </Pressable>
          ))}
        </View>

        {/* Loading */}
        {isLoading && (
          <View style={styles.centeredBox}>
            <ActivityIndicator color={colors.primaryLight} size="large" />
          </View>
        )}

        {/* Error */}
        {hasError && !isLoading && (
          <View style={styles.centeredBox}>
            <Text style={styles.errorText}>Erro ao carregar dados</Text>
            <Pressable
              style={styles.retryBtn}
              onPress={() => { refetchSpending(); refetchTrends(); }}
              accessibilityRole="button"
            >
              <Text style={styles.retryBtnText}>Tentar novamente</Text>
            </Pressable>
          </View>
        )}

        {/* Spending by category — Pie chart */}
        {!isLoading && !hasError && (
          <>
            <View style={styles.card}>
              <Text style={styles.cardTitle}>Gastos por categoria</Text>

              {hasSpendingData ? (
                <>
                  <View style={styles.chartCenter}>
                    <PieChart
                      data={pieData}
                      donut
                      radius={100}
                      innerRadius={60}
                      centerLabelComponent={() => (
                        <Text style={styles.pieCenterLabel}>
                          {formatBRL(pieData.reduce((s, d) => s + d.value, 0))}
                        </Text>
                      )}
                      onPress={(item: typeof pieData[number]) => {
                        // Interaction: tap shows label — gifted-charts handles focus internally
                        void item;
                      }}
                    />
                  </View>

                  {/* Legend */}
                  <View style={styles.legend}>
                    {spendingData?.categories.map((c) => (
                      <View key={c.name} style={styles.legendItem}>
                        <View style={[styles.legendDot, { backgroundColor: c.color }]} />
                        <Text style={styles.legendLabel} numberOfLines={1}>
                          {c.name}
                        </Text>
                        <Text style={styles.legendValue}>{formatBRL(c.total)}</Text>
                      </View>
                    ))}
                  </View>
                </>
              ) : (
                <EmptyState />
              )}
            </View>

            {/* Monthly trends — Bar chart */}
            <View style={styles.card}>
              <Text style={styles.cardTitle}>Gastos mensais</Text>

              {hasTrendsData ? (
                <ScrollView horizontal showsHorizontalScrollIndicator={false}>
                  <BarChart
                    data={barData}
                    barWidth={28}
                    spacing={12}
                    roundedTop
                    xAxisThickness={1}
                    yAxisThickness={0}
                    xAxisColor={colors.border}
                    yAxisTextStyle={{ color: colors.textMuted, fontSize: 10 }}
                    xAxisLabelTextStyle={{ color: colors.textMuted, fontSize: 10 }}
                    noOfSections={4}
                    maxValue={Math.max(...barData.map((d) => d.value), 1) * 1.2}
                    hideRules
                    barBorderRadius={4}
                    isAnimated
                    width={Math.max(barData.length * 44, 280)}
                  />
                </ScrollView>
              ) : (
                <EmptyState />
              )}
            </View>
          </>
        )}
      </ScrollView>
    </SafeAreaView>
  );
}

function EmptyState() {
  return (
    <View style={styles.emptyBox}>
      <Text style={styles.emptyIcon}>📊</Text>
      <Text style={styles.emptyTitle}>Sem dados ainda</Text>
      <Text style={styles.emptySubtitle}>
        Adicione transações para visualizar relatórios.
      </Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.background,
  },
  scroll: {
    padding: spacing.md,
    paddingBottom: spacing.xxl,
  },
  screenTitle: {
    fontSize: typography.fontSize.xxl,
    fontWeight: typography.fontWeight.bold,
    color: colors.text,
    marginBottom: spacing.md,
  },
  periodRow: {
    flexDirection: 'row',
    gap: spacing.sm,
    marginBottom: spacing.lg,
  },
  periodBtn: {
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.xs + 2,
    borderRadius: borderRadius.md,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
  },
  periodBtnActive: {
    backgroundColor: colors.primary,
    borderColor: colors.primary,
  },
  periodBtnText: {
    fontSize: typography.fontSize.sm,
    fontWeight: typography.fontWeight.semibold,
    color: colors.textMuted,
  },
  periodBtnTextActive: {
    color: colors.white,
  },
  centeredBox: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: spacing.xxl,
    gap: spacing.md,
  },
  errorText: {
    color: colors.error,
    fontSize: typography.fontSize.md,
  },
  retryBtn: {
    paddingHorizontal: spacing.lg,
    paddingVertical: spacing.sm,
    backgroundColor: colors.primary,
    borderRadius: borderRadius.md,
  },
  retryBtnText: {
    color: colors.white,
    fontWeight: typography.fontWeight.semibold,
    fontSize: typography.fontSize.md,
  },
  card: {
    backgroundColor: colors.surface,
    borderRadius: borderRadius.md + 4,
    padding: spacing.md,
    marginBottom: spacing.md,
  },
  cardTitle: {
    fontSize: typography.fontSize.lg,
    fontWeight: typography.fontWeight.semibold,
    color: colors.text,
    marginBottom: spacing.md,
  },
  chartCenter: {
    alignItems: 'center',
    marginBottom: spacing.md,
  },
  pieCenterLabel: {
    fontSize: typography.fontSize.sm,
    fontWeight: typography.fontWeight.bold,
    color: colors.text,
    textAlign: 'center',
  },
  legend: {
    gap: spacing.sm,
  },
  legendItem: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
  },
  legendDot: {
    width: 10,
    height: 10,
    borderRadius: 5,
    flexShrink: 0,
  },
  legendLabel: {
    flex: 1,
    fontSize: typography.fontSize.sm,
    color: colors.textSubtle,
  },
  legendValue: {
    fontSize: typography.fontSize.sm,
    fontWeight: typography.fontWeight.semibold,
    color: colors.text,
  },
  emptyBox: {
    alignItems: 'center',
    paddingVertical: spacing.xl,
    gap: spacing.sm,
  },
  emptyIcon: {
    fontSize: 40,
  },
  emptyTitle: {
    fontSize: typography.fontSize.lg,
    fontWeight: typography.fontWeight.semibold,
    color: colors.textSubtle,
  },
  emptySubtitle: {
    fontSize: typography.fontSize.sm,
    color: colors.textMuted,
    textAlign: 'center',
  },
});
