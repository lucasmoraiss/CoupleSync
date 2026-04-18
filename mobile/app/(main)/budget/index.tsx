// AC-111, AC-112, AC-119: Budget setup screen — monthly income + category allocations
import React, { useState, useCallback, useEffect } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  ScrollView,
  TouchableOpacity,
  TextInput,
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
} from 'react-native';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { budgetApiClient, isCoupleRequiredError } from '@/services/apiClient';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';
import { useToast } from '@/components/Toast/useToast';

// ─── Design tokens ────────────────────────────────────────────────────────────
const BG = colors.background;
const CARD = colors.surface;
const PRIMARY = colors.primary;
const ACCENT = colors.primaryLight;
const TEXT = colors.text;
const MUTED = colors.textMuted;
const BORDER = colors.border;
const ERROR = colors.error;
const SUCCESS = colors.success;
const WARNING = colors.warning;

// ─── Helpers ──────────────────────────────────────────────────────────────────
function formatBRL(amount: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(amount);
}

function parseBRLInput(value: string): number {
  const sanitized = value.replace(/[^\d]/g, '');
  return sanitized ? Number(sanitized) / 100 : 0;
}

function formatBRLInput(cents: number): string {
  return new Intl.NumberFormat('pt-BR', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(cents / 100);
}

function currentMonthISO(): string {
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, '0');
  return `${y}-${m}`;
}

const MONTH_NAMES = [
  'Janeiro', 'Fevereiro', 'Março', 'Abril', 'Maio', 'Junho',
  'Julho', 'Agosto', 'Setembro', 'Outubro', 'Novembro', 'Dezembro',
];

function formatMonthDisplay(isoMonth: string): string {
  const parts = isoMonth.split('-');
  const year = parts[0];
  const monthIndex = Number(parts[1]) - 1;
  return `${MONTH_NAMES[monthIndex]} ${year}`;
}

// ─── Types ────────────────────────────────────────────────────────────────────
interface AllocationRow {
  id: string;
  category: string;
  amountCents: number;
}

let _rowIdCounter = 0;
function nextRowId(): string {
  _rowIdCounter += 1;
  return String(_rowIdCounter);
}

// ─── Progress Bar ─────────────────────────────────────────────────────────────
function CategoryProgressBar({ percentage }: { percentage: number }) {
  const capped = Math.min(percentage, 100);
  const fillColor =
    percentage >= 100 ? ERROR : percentage >= 80 ? WARNING : SUCCESS;
  return (
    <View style={progressStyles.track}>
      <View style={[progressStyles.fill, { width: `${capped}%` as any, backgroundColor: fillColor }]} />
    </View>
  );
}

const progressStyles = StyleSheet.create({
  track: {
    height: 8,
    borderRadius: 4,
    backgroundColor: BORDER,
    overflow: 'hidden',
    marginVertical: 6,
  },
  fill: {
    height: 8,
    borderRadius: 4,
  },
});

// ─── Screen ───────────────────────────────────────────────────────────────────
export default function BudgetSetupScreen() {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const [month] = useState(currentMonthISO());
  const [incomeCents, setIncomeCents] = useState(0);
  const [allocations, setAllocations] = useState<AllocationRow[]>([]);
  const [successMsg, setSuccessMsg] = useState('');

  // Auto-clear success message
  useEffect(() => {
    if (successMsg) {
      const t = setTimeout(() => setSuccessMsg(''), 3000);
      return () => clearTimeout(t);
    }
  }, [successMsg]);

  // Load current budget plan (404 = not found, treated as empty)
  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['budget', 'current'],
    queryFn: () => budgetApiClient.getCurrent().then((r) => r.data),
    retry: false,
  });

  // Pre-fill form when data loads
  useEffect(() => {
    if (data) {
      setIncomeCents(Math.round(data.grossIncome * 100));
      setAllocations(
        data.allocations.map((a) => ({
          id: nextRowId(),
          category: a.category,
          amountCents: Math.round(a.allocatedAmount * 100),
        }))
      );
    }
  }, [data]);

  // Budget gap
  const totalAllocatedCents = allocations.reduce((sum, r) => sum + r.amountCents, 0);
  const gapCents = incomeCents - totalAllocatedCents;

  // Save mutation
  const saveMutation = useMutation({
    mutationFn: async () => {
      const planRes = await budgetApiClient.upsertPlan({
        month,
        grossIncome: incomeCents / 100,
        currency: 'BRL',
      });
      const planId = planRes.data.id;
      await budgetApiClient.replaceAllocations(planId, {
        allocations: allocations.map((r) => ({
          category: r.category.trim() || 'Sem categoria',
          allocatedAmount: r.amountCents / 100,
          currency: 'BRL',
        })),
      });
    },
    onSuccess: () => {
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      setSuccessMsg('Orçamento salvo com sucesso!');
      queryClient.invalidateQueries({ queryKey: ['budget'] });
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      queryClient.invalidateQueries({ queryKey: ['budget'] });
      toast.error('Não foi possível salvar o orçamento. Tente novamente.');
    },
  });

  const addRow = useCallback(() => {
    setAllocations((prev) => [...prev, { id: nextRowId(), category: '', amountCents: 0 }]);
  }, []);

  const removeRow = useCallback((id: string) => {
    setAllocations((prev) => prev.filter((r) => r.id !== id));
  }, []);

  const updateCategory = useCallback((id: string, value: string) => {
    setAllocations((prev) =>
      prev.map((r) => (r.id === id ? { ...r, category: value } : r))
    );
  }, []);

  const updateAmount = useCallback((id: string, raw: string) => {
    const digits = raw.replace(/[^\d]/g, '');
    const cents = digits ? Number(digits) : 0;
    setAllocations((prev) =>
      prev.map((r) => (r.id === id ? { ...r, amountCents: cents } : r))
    );
  }, []);

  if (isLoading) {
    return (
      <SafeAreaView style={styles.container}>
        <LoadingState />
      </SafeAreaView>
    );
  }

  if (isError) {
    return (
      <SafeAreaView style={styles.container}>
        <ErrorState onRetry={() => refetch()} />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
      >
        <ScrollView
          style={styles.flex}
          contentContainerStyle={styles.content}
          showsVerticalScrollIndicator={false}
          keyboardShouldPersistTaps="handled"
        >
          {/* Header */}
          <Text style={styles.screenTitle}>Orçamento Mensal</Text>

          {/* Month display */}
          <View style={styles.card}>
            <Text style={styles.cardLabel}>Mês</Text>
            <Text style={styles.monthText}>{formatMonthDisplay(month)}</Text>
          </View>

          {/* Gross income */}
          <View style={styles.card}>
            <Text style={styles.cardLabel}>Renda Bruta</Text>
            <View style={styles.inputRow}>
              <Text style={styles.currencySymbol}>R$</Text>
              <TextInput
                style={styles.amountInput}
                value={incomeCents > 0 ? formatBRLInput(incomeCents) : ''}
                onChangeText={(raw) => {
                  const digits = raw.replace(/[^\d]/g, '');
                  setIncomeCents(digits ? Number(digits) : 0);
                }}
                placeholder="0,00"
                placeholderTextColor={MUTED}
                keyboardType="numeric"
                accessibilityLabel="Renda bruta mensal"
              />
            </View>
          </View>

          {/* Allocations section */}
          <View style={styles.sectionHeader}>
            <Text style={styles.sectionTitle}>Categorias</Text>
          </View>

          {allocations.map((row) => (
            <View key={row.id} style={styles.allocationRow}>
              <TextInput
                style={styles.categoryInput}
                value={row.category}
                onChangeText={(v) => updateCategory(row.id, v)}
                placeholder="Categoria"
                placeholderTextColor={MUTED}
                accessibilityLabel="Nome da categoria"
              />
              <View style={styles.amountCell}>
                <Text style={styles.currencySymbolSmall}>R$</Text>
                <TextInput
                  style={styles.allocationAmountInput}
                  value={row.amountCents > 0 ? formatBRLInput(row.amountCents) : ''}
                  onChangeText={(v) => updateAmount(row.id, v)}
                  placeholder="0,00"
                  placeholderTextColor={MUTED}
                  keyboardType="numeric"
                  accessibilityLabel="Valor alocado"
                />
              </View>
              <TouchableOpacity
                onPress={() => removeRow(row.id)}
                accessibilityLabel="Remover categoria"
                style={styles.deleteBtn}
              >
                <Ionicons name="trash-outline" size={18} color={ERROR} />
              </TouchableOpacity>
            </View>
          ))}

          {/* Add row button */}
          <TouchableOpacity
            style={[styles.addBtn, allocations.length >= 20 && { opacity: 0.4 }]}
            onPress={addRow}
            disabled={allocations.length >= 20}
            accessibilityLabel="Adicionar categoria"
          >
            <Ionicons name="add-circle-outline" size={20} color={ACCENT} />
            <Text style={styles.addBtnText}>Adicionar categoria</Text>
          </TouchableOpacity>

          {/* Budget gap (local preview when no saved data, otherwise shown in overview) */}
          {!data && (
            <View style={styles.gapCard}>
              <Text style={styles.gapLabel}>Saldo livre:</Text>
              <Text style={[styles.gapValue, gapCents < 0 ? styles.gapNegative : styles.gapPositive]}>
                {formatBRL(gapCents / 100)}
              </Text>
            </View>
          )}

          {/* Feedback messages */}
          {successMsg ? (
            <Text style={styles.successMsg}>{successMsg}</Text>
          ) : null}
          {isError && !data ? (
            <Text style={styles.infoMsg}>
              Nenhum orçamento encontrado para este mês. Preencha e salve.
            </Text>
          ) : null}

          {/* Max 20 allocations hint */}
          {allocations.length >= 20 && (
            <Text style={[styles.infoMsg, { color: WARNING }]}>Máximo de 20 categorias atingido.</Text>
          )}

          {/* Save button */}
          <TouchableOpacity
            style={[styles.saveBtn, saveMutation.isPending && styles.saveBtnDisabled]}
            onPress={() => saveMutation.mutate()}
            disabled={saveMutation.isPending}
            accessibilityLabel="Salvar orçamento"
          >
            {saveMutation.isPending ? (
              <ActivityIndicator size="small" color={TEXT} />
            ) : (
              <Text style={styles.saveBtnText}>Salvar Orçamento</Text>
            )}
          </TouchableOpacity>

          {/* Budget Overview Section — shown when a plan is loaded */}
          {data && data.allocations && data.allocations.length > 0 && (
            <>
              <View style={styles.sectionHeader}>
                <Text style={styles.sectionTitle}>Visão Geral do Orçamento</Text>
              </View>

              {data.allocations.map((alloc) => {
                const percentage =
                  alloc.allocatedAmount > 0
                    ? (alloc.actualSpent / alloc.allocatedAmount) * 100
                    : 0;
                const overspent = alloc.remaining < 0;
                return (
                  <View key={alloc.id} style={styles.overviewCard}>
                    <Text style={styles.overviewCategory}>{alloc.category}</Text>
                    <CategoryProgressBar percentage={percentage} />
                    <Text style={styles.overviewSpent}>
                      {`Gasto ${formatBRL(alloc.actualSpent)} de ${formatBRL(alloc.allocatedAmount)}`}
                    </Text>
                    {overspent ? (
                      <Text style={styles.overviewExceeded}>
                        {`Excedido em ${formatBRL(Math.abs(alloc.remaining))}`}
                      </Text>
                    ) : (
                      <Text style={styles.overviewRemaining}>
                        {`Restante: ${formatBRL(alloc.remaining)}`}
                      </Text>
                    )}
                  </View>
                );
              })}

              {/* Budget Gap */}
              <View style={styles.gapCard}>
                <Text style={styles.gapLabel}>Saldo livre:</Text>
                <View style={{ alignItems: 'flex-end' }}>
                  <Text
                    style={[
                      styles.gapValue,
                      data.budgetGap < 0 ? styles.gapNegative : styles.gapPositive,
                    ]}
                  >
                    {formatBRL(data.budgetGap)}
                  </Text>
                  {data.budgetGap < 0 && (
                    <Text style={styles.overviewExceeded}>Orçamento excedido</Text>
                  )}
                </View>
              </View>
            </>
          )}

          <View style={styles.bottomSpacer} />
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

// ─── Styles ───────────────────────────────────────────────────────────────────
const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { flex: 1, backgroundColor: BG },
  center: { flex: 1, justifyContent: 'center', alignItems: 'center' },
  loadingText: { color: MUTED, marginTop: 12, fontSize: 14 },
  content: { padding: 16 },
  screenTitle: { fontSize: 22, fontWeight: '700', color: TEXT, marginBottom: 16 },
  card: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    marginBottom: 12,
    borderWidth: 1,
    borderColor: BORDER,
  },
  cardLabel: {
    fontSize: 12,
    color: MUTED,
    marginBottom: 6,
    fontWeight: '600',
    textTransform: 'uppercase',
    letterSpacing: 0.5,
  },
  monthText: { fontSize: 18, color: TEXT, fontWeight: '600' },
  inputRow: { flexDirection: 'row', alignItems: 'center' },
  currencySymbol: { fontSize: 18, color: ACCENT, marginRight: 8, fontWeight: '700' },
  currencySymbolSmall: { fontSize: 14, color: ACCENT, marginRight: 4, fontWeight: '600' },
  amountInput: { flex: 1, fontSize: 20, color: TEXT, padding: 0 },
  sectionHeader: { flexDirection: 'row', alignItems: 'center', marginBottom: 8, marginTop: 4 },
  sectionTitle: {
    fontSize: 14,
    color: MUTED,
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: 0.5,
  },
  allocationRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: CARD,
    borderRadius: 10,
    padding: 10,
    marginBottom: 8,
    borderWidth: 1,
    borderColor: BORDER,
  },
  categoryInput: { flex: 2, fontSize: 14, color: TEXT, marginRight: 8 },
  amountCell: { flex: 1.5, flexDirection: 'row', alignItems: 'center' },
  allocationAmountInput: { flex: 1, fontSize: 14, color: TEXT, padding: 0 },
  deleteBtn: { padding: 4, marginLeft: 8 },
  addBtn: { flexDirection: 'row', alignItems: 'center', padding: 10, marginBottom: 8 },
  addBtnText: { color: ACCENT, fontSize: 14, fontWeight: '600', marginLeft: 6 },
  gapCard: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    marginBottom: 12,
    borderWidth: 1,
    borderColor: BORDER,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  gapLabel: { fontSize: 14, color: MUTED, fontWeight: '600' },
  gapValue: { fontSize: 18, fontWeight: '700' },
  gapPositive: { color: SUCCESS },
  gapNegative: { color: ERROR },
  successMsg: { color: SUCCESS, textAlign: 'center', marginBottom: 12, fontSize: 14, fontWeight: '600' },
  infoMsg: { color: MUTED, textAlign: 'center', marginBottom: 12, fontSize: 13 },
  saveBtn: {
    backgroundColor: PRIMARY,
    borderRadius: 12,
    padding: 16,
    alignItems: 'center',
    marginBottom: 8,
  },
  saveBtnDisabled: { opacity: 0.6 },
  saveBtnText: { color: TEXT, fontSize: 16, fontWeight: '700' },
  bottomSpacer: { height: 40 },
  overviewCard: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    marginBottom: 10,
    borderWidth: 1,
    borderColor: BORDER,
  },
  overviewCategory: { fontSize: 14, color: TEXT, fontWeight: '700' },
  overviewSpent: { fontSize: 12, color: MUTED, marginTop: 2 },
  overviewRemaining: { fontSize: 12, color: SUCCESS, marginTop: 2 },
  overviewExceeded: { fontSize: 12, color: ERROR, fontWeight: '600', marginTop: 2 },
});
