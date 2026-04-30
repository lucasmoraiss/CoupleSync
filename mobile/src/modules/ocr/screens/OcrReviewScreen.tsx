// AC-124, AC-126, AC-127: OCR review screen — candidates with checkboxes, edit fields, confirm
import React, { useState, useCallback, useEffect, useRef } from 'react';
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
import { router } from 'expo-router';
import * as Haptics from 'expo-haptics';
import { ocrApiClient, isCoupleRequiredError } from '@/services/apiClient';
import type { OcrCandidateResponse } from '@/types/api';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';
import { useToast } from '@/components/Toast/useToast';

// ─── Design tokens ────────────────────────────────────────────────────────────
const BG = colors.background;
const CARD = colors.surface;
const PRIMARY = colors.primary;
// ACCENT is intentionally declared for future use
// eslint-disable-next-line @typescript-eslint/no-unused-vars
const ACCENT = colors.primaryLight;
const TEXT = colors.text;
const MUTED = colors.textMuted;
const BORDER = colors.border;
const ERROR = colors.error;
const SUCCESS = colors.success;
const WARNING = colors.warning;

// ─── Helpers ──────────────────────────────────────────────────────────────────
function parseBRLInput(value: string): number {
  const digits = value.replace(/[^\d]/g, '');
  return digits ? Number(digits) : 0;
}

function formatBRLInput(cents: number): string {
  return new Intl.NumberFormat('pt-BR', {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(cents / 100);
}

function formatDate(isoDate: string): string {
  const d = new Date(isoDate);
  const day = String(d.getUTCDate()).padStart(2, '0');
  const month = String(d.getUTCMonth() + 1).padStart(2, '0');
  const year = d.getUTCFullYear();
  return `${day}/${month}/${year}`;
}

// ─── Local row state ──────────────────────────────────────────────────────────
interface CandidateRow {
  index: number;
  selected: boolean;
  description: string;
  amountCents: number;
  date: string;
  confidence: number;
  duplicateSuspected: boolean;
  category: string;
}

function candidateToRow(c: OcrCandidateResponse): CandidateRow {
  return {
    index: c.index,
    selected: true,
    description: c.description,
    amountCents: Math.round(c.amount * 100),
    date: c.date,
    confidence: c.confidence,
    duplicateSuspected: c.duplicateSuspected,
    category: c.suggestedCategory ?? '',
  };
}

// ─── Screen ───────────────────────────────────────────────────────────────────
interface Props {
  uploadId: string;
}

export default function OcrReviewScreen({ uploadId }: Props) {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const [rows, setRows] = useState<CandidateRow[]>([]);
  const [successMsg, setSuccessMsg] = useState('');
  const initializedRef = useRef(false);

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['ocr-results', uploadId],
    queryFn: () => ocrApiClient.getResults(uploadId).then((r) => r.data),
    staleTime: Infinity,
    retry: 1,
  });

  // Seed editable rows once on first load; guard prevents resetting user edits
  useEffect(() => {
    if (data && !initializedRef.current) {
      initializedRef.current = true;
      setRows(data.candidates.map(candidateToRow));
    }
  }, [data]);

  const confirmMutation = useMutation({
    mutationFn: () => {
      const selectedIndices = rows.filter((r) => r.selected).map((r) => r.index);
      const categoryOverrides = rows
        .filter((r) => r.selected && r.category.trim().length > 0)
        .map((r) => ({ index: r.index, category: r.category.trim() }));
      return ocrApiClient.confirm(uploadId, { selectedIndices, categoryOverrides });
    },
    onSuccess: (res) => {
      const count = res.data.transactionsCreated;
      queryClient.invalidateQueries({ queryKey: ['transactions'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      queryClient.invalidateQueries({ queryKey: ['reports'] });
      queryClient.invalidateQueries({ queryKey: ['budget'] });
      const label = count === 1 ? 'transação importada' : 'transações importadas';
      setSuccessMsg(`${count} ${label} com sucesso!`);
      setTimeout(() => {
        router.replace('/(main)/transactions' as any);
      }, 1200);
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error('Não foi possível importar as transações. Tente novamente.');
    },
  });

  const handleConfirm = useCallback(async () => {
    const anySelected = rows.some((r) => r.selected);
    if (!anySelected) {
      toast.warning('Selecione ao menos uma transação para importar.');
      return;
    }
    await Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    confirmMutation.mutate();
  }, [rows, confirmMutation]);

  const toggleAll = useCallback(() => {
    const allSelected = rows.every((r) => r.selected);
    setRows((prev) => prev.map((r) => ({ ...r, selected: !allSelected })));
  }, [rows]);

  const toggleRow = useCallback((index: number) => {
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, selected: !r.selected } : r))
    );
  }, []);

  const updateDescription = useCallback((index: number, text: string) => {
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, description: text } : r))
    );
  }, []);

  const updateAmount = useCallback((index: number, raw: string) => {
    const cents = parseBRLInput(raw);
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, amountCents: cents } : r))
    );
  }, []);

  const updateCategory = useCallback((index: number, text: string) => {
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, category: text } : r))
    );
  }, []);

  const allSelected = rows.length > 0 && rows.every((r) => r.selected);

  // ─── Loading state ─────────────────────────────────────────────────────────
  if (isLoading) {
    return (
      <SafeAreaView style={styles.container}>
        <LoadingState message="Carregando resultados..." />
      </SafeAreaView>
    );
  }

  // ─── Error state ───────────────────────────────────────────────────────────
  if (isError) {
    return (
      <SafeAreaView style={styles.container}>
        <ErrorState
          message="Erro ao carregar resultados."
          onRetry={() => refetch()}
        />
      </SafeAreaView>
    );
  }

  // ─── Main render ───────────────────────────────────────────────────────────
  return (
    <SafeAreaView style={styles.container}>
      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
      >
        {/* Header */}
        <View style={styles.header}>
          <Text style={styles.title}>Revisão de Importação</Text>
          <TouchableOpacity onPress={toggleAll} style={styles.toggleAllBtn}>
            <Text style={styles.toggleAllText}>
              {allSelected ? 'Desmarcar Todos' : 'Selecionar Todos'}
            </Text>
          </TouchableOpacity>
        </View>

        {/* Success banner */}
        {successMsg ? (
          <View style={styles.successBanner}>
            <Ionicons name="checkmark-circle" size={18} color={SUCCESS} />
            <Text style={styles.successText}>{successMsg}</Text>
          </View>
        ) : null}

        <ScrollView
          style={styles.flex}
          contentContainerStyle={styles.scrollContent}
          keyboardShouldPersistTaps="handled"
          showsVerticalScrollIndicator={false}
        >
          {rows.map((row) => (
            <View
              key={row.index}
              style={[styles.card, row.duplicateSuspected && styles.cardWarning]}
            >
              {/* Top row: checkbox + date + confidence */}
              <View style={styles.cardTopRow}>
                <TouchableOpacity
                  onPress={() => toggleRow(row.index)}
                  style={styles.checkboxArea}
                  accessibilityRole="checkbox"
                  accessibilityState={{ checked: row.selected }}
                >
                  <View style={[styles.checkbox, row.selected && styles.checkboxSelected]}>
                    {row.selected && <Ionicons name="checkmark" size={14} color={TEXT} />}
                  </View>
                </TouchableOpacity>
                <Text style={styles.dateText}>{formatDate(row.date)}</Text>
                <Text style={styles.confidenceText}>
                  {'Confiança: ' + String(Math.round(row.confidence * 100)) + '%'}
                </Text>
              </View>

              {/* Duplicate warning */}
              {row.duplicateSuspected && (
                <View style={styles.duplicateWarning}>
                  <Ionicons name="warning-outline" size={14} color={WARNING} />
                  <Text style={styles.duplicateText}>Possível duplicata</Text>
                </View>
              )}

              {/* Editable description */}
              <TextInput
                style={styles.input}
                value={row.description}
                onChangeText={(t) => updateDescription(row.index, t)}
                placeholder="Descrição"
                placeholderTextColor={MUTED}
              />

              {/* Editable amount */}
              <View style={styles.amountRow}>
                <Text style={styles.currencyLabel}>R$</Text>
                <TextInput
                  style={[styles.input, styles.amountInput]}
                  value={formatBRLInput(row.amountCents)}
                  keyboardType="numeric"
                  onChangeText={(t) => updateAmount(row.index, t)}
                  placeholder="0,00"
                  placeholderTextColor={MUTED}
                />
              </View>

              {/* Category chip — pre-filled from AI suggestion, editable */}
              <View style={styles.categoryRow}>
                <Text style={styles.categoryLabel}>Categoria:</Text>
                <TextInput
                  style={[styles.input, styles.categoryInput]}
                  value={row.category}
                  onChangeText={(t) => updateCategory(row.index, t)}
                  placeholder="Ex: Alimentação"
                  placeholderTextColor={MUTED}
                  autoCapitalize="sentences"
                />
              </View>
            </View>
          ))}
          <View style={styles.scrollPadding} />
        </ScrollView>

        {/* Confirm button */}
        <View style={styles.footer}>
          <TouchableOpacity
            style={[styles.confirmBtn, confirmMutation.isPending && styles.confirmBtnDisabled]}
            onPress={handleConfirm}
            disabled={confirmMutation.isPending}
          >
            {confirmMutation.isPending ? (
              <ActivityIndicator color={TEXT} size="small" />
            ) : (
              <Text style={styles.confirmBtnText}>Confirmar Importação</Text>
            )}
          </TouchableOpacity>
        </View>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

// ─── Styles ───────────────────────────────────────────────────────────────────
const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: BG },
  flex: { flex: 1 },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: 12 },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: 16,
    paddingVertical: 14,
    borderBottomWidth: 1,
    borderBottomColor: BORDER,
  },
  title: { fontSize: 18, fontWeight: '700', color: TEXT },
  toggleAllBtn: { paddingVertical: 6, paddingHorizontal: 10 },
  toggleAllText: { fontSize: 13, color: PRIMARY, fontWeight: '600' },
  successBanner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    backgroundColor: colors.successDark,
    paddingHorizontal: 16,
    paddingVertical: 10,
  },
  successText: { fontSize: 13, color: SUCCESS, flex: 1 },
  scrollContent: { padding: 16, gap: 12 },
  card: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    borderWidth: 1,
    borderColor: BORDER,
    gap: 10,
  },
  cardWarning: { borderColor: WARNING },
  cardTopRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  checkboxArea: { padding: 2 },
  checkbox: {
    width: 22,
    height: 22,
    borderRadius: 6,
    borderWidth: 2,
    borderColor: BORDER,
    alignItems: 'center',
    justifyContent: 'center',
  },
  checkboxSelected: { backgroundColor: PRIMARY, borderColor: PRIMARY },
  dateText: { flex: 1, fontSize: 13, color: MUTED },
  confidenceText: { fontSize: 12, color: MUTED, fontStyle: 'italic' },
  duplicateWarning: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  duplicateText: { fontSize: 12, color: WARNING, fontWeight: '600' },
  input: {
    backgroundColor: BG,
    borderWidth: 1,
    borderColor: BORDER,
    borderRadius: 8,
    color: TEXT,
    paddingHorizontal: 12,
    paddingVertical: 8,
    fontSize: 14,
  },
  amountRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  currencyLabel: { fontSize: 14, color: MUTED, fontWeight: '600' },
  amountInput: { flex: 1 },
  categoryRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  categoryLabel: { fontSize: 13, color: MUTED, fontWeight: '600', minWidth: 72 },
  categoryInput: { flex: 1, fontSize: 13 },
  footer: {
    padding: 16,
    borderTopWidth: 1,
    borderTopColor: BORDER,
  },
  confirmBtn: {
    backgroundColor: PRIMARY,
    borderRadius: 12,
    paddingVertical: 14,
    alignItems: 'center',
  },
  confirmBtnDisabled: { opacity: 0.5 },
  confirmBtnText: { color: TEXT, fontSize: 16, fontWeight: '700' },
  mutedText: { color: MUTED, fontSize: 14 },
  errorText: { color: ERROR, fontSize: 14 },
  scrollPadding: { height: 24 },
});
