// AC-124, AC-126, AC-127: OCR review screen — candidates with checkboxes, edit fields, confirm
import { getApiErrorMessage } from '@/services/apiError';
import React, { useState, useCallback, useEffect, useMemo } from 'react';
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
import {
  buildOcrConfirmRequest,
  creditsLabel,
  emptyReviewSession,
  formatBRLInput,
  parseBRLInput,
  seedReviewRows,
  sessionFor,
  validateReviewRows,
  type ReviewRow,
  type ReviewSession,
} from '@/modules/ocr/confirmRequest';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';
import { useToast } from '@/components/Toast/useToast';
import { useCategories } from '@/modules/transactions/useCategories';

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
function formatDate(isoDate: string): string {
  const d = new Date(isoDate);
  const day = String(d.getUTCDate()).padStart(2, '0');
  const month = String(d.getUTCMonth() + 1).padStart(2, '0');
  const year = d.getUTCFullYear();
  return `${day}/${month}/${year}`;
}

// ─── Local row state ──────────────────────────────────────────────────────────
// Row shape, BRL helpers and the confirm body live in '@/modules/ocr/confirmRequest' (pure, unit-tested).

// ─── Screen ───────────────────────────────────────────────────────────────────
interface Props {
  uploadId: string;
}

export default function OcrReviewScreen({ uploadId }: Props) {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const categories = useCategories();
  // The rows belong to one import: sessionFor() drops them as soon as the uploadId changes, so a screen
  // that stays mounted can never show (or confirm) the lines of the previous import.
  const [storedSession, setStoredSession] = useState<ReviewSession>(() => emptyReviewSession(uploadId));
  const session = sessionFor(storedSession, uploadId);
  const rows = session.rows;
  const [successMsg, setSuccessMsg] = useState('');
  // Field errors are shown only after the first confirm attempt, then update live
  const [showErrors, setShowErrors] = useState(false);

  useEffect(() => {
    setSuccessMsg('');
    setShowErrors(false);
  }, [uploadId]);

  const setRows = useCallback(
    (update: (previous: ReviewRow[]) => ReviewRow[]) => {
      setStoredSession((previous) => {
        const base = sessionFor(previous, uploadId);
        return { ...base, rows: update(base.rows) };
      });
    },
    [uploadId],
  );

  const { data, isLoading, isError, error: loadError, refetch } = useQuery({
    queryKey: ['ocr-results', uploadId],
    queryFn: () => ocrApiClient.getResults(uploadId).then((r) => r.data),
    staleTime: Infinity,
    retry: 1,
  });

  // Seed editable rows once per import; the guard keeps the user's edits on later renders.
  useEffect(() => {
    if (data && !session.seeded) {
      setStoredSession({ uploadId, rows: seedReviewRows(data.candidates), seeded: true });
    }
  }, [data, session.seeded, uploadId]);

  const creditsCount = data?.creditsCount ?? data?.credits?.length ?? 0;

  // Validation of the edited fields (description / amount) of the selected rows
  const rowErrors = useMemo(
    () => validateReviewRows(rows, data?.candidates ?? []),
    [rows, data],
  );

  const confirmMutation = useMutation({
    // Sends the user's edits (candidateEdits) along with the selection and category overrides
    mutationFn: () =>
      ocrApiClient.confirm(uploadId, buildOcrConfirmRequest(rows, data?.candidates ?? [])),
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
      toast.error(getApiErrorMessage(error, 'Não foi possível importar as transações. Tente novamente.'));
    },
  });

  const handleConfirm = useCallback(async () => {
    const anySelected = rows.some((r) => r.selected);
    if (!anySelected) {
      toast.warning('Selecione ao menos uma transação para importar.');
      return;
    }
    if (Object.keys(rowErrors).length > 0) {
      setShowErrors(true);
      toast.warning('Corrija os campos destacados antes de importar.');
      return;
    }
    await Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    confirmMutation.mutate();
  }, [rows, rowErrors, confirmMutation]);

  const toggleAll = useCallback(() => {
    const allSelected = rows.every((r) => r.selected);
    setRows((prev) => prev.map((r) => ({ ...r, selected: !allSelected })));
  }, [rows, setRows]);

  const toggleRow = useCallback((index: number) => {
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, selected: !r.selected } : r))
    );
  }, [setRows]);

  const updateDescription = useCallback((index: number, text: string) => {
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, description: text } : r))
    );
  }, [setRows]);

  const updateAmount = useCallback((index: number, raw: string) => {
    const cents = parseBRLInput(raw);
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, amountCents: cents } : r))
    );
  }, [setRows]);

  // Tocar na categoria escolhida de novo a limpa (a API usa "Outros" quando não há categoria).
  const updateCategory = useCallback((index: number, key: string) => {
    setRows((prev) =>
      prev.map((r) => (r.index === index ? { ...r, category: r.category === key ? '' : key } : r))
    );
  }, [setRows]);

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
          message={getApiErrorMessage(loadError, 'Erro ao carregar resultados.')}
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
          {rows.map((row) => {
            const errors = showErrors ? rowErrors[row.index] : undefined;
            return (
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
                style={[styles.input, errors?.description ? styles.inputError : null]}
                value={row.description}
                onChangeText={(t) => updateDescription(row.index, t)}
                placeholder="Descrição"
                placeholderTextColor={MUTED}
              />
              {errors?.description ? (
                <Text style={styles.fieldErrorText}>{errors.description}</Text>
              ) : null}

              {/* Editable amount */}
              <View style={styles.amountRow}>
                <Text style={styles.currencyLabel}>R$</Text>
                <TextInput
                  style={[styles.input, styles.amountInput, errors?.amount ? styles.inputError : null]}
                  value={formatBRLInput(row.amountCents)}
                  keyboardType="numeric"
                  onChangeText={(t) => updateAmount(row.index, t)}
                  placeholder="0,00"
                  placeholderTextColor={MUTED}
                />
              </View>
              {errors?.amount ? (
                <Text style={styles.fieldErrorText}>{errors.amount}</Text>
              ) : null}

              {/* Categoria — pré-preenchida pela sugestão; só as da lista canônica */}
              <Text style={styles.categoryLabel}>Categoria:</Text>
              <ScrollView horizontal showsHorizontalScrollIndicator={false} keyboardShouldPersistTaps="handled">
                <View style={styles.categoryRow}>
                  {categories.map((c) => {
                    const active = row.category === c.value;
                    return (
                      <TouchableOpacity
                        key={c.value}
                        style={[styles.categoryChip, active && styles.categoryChipActive]}
                        onPress={() => updateCategory(row.index, c.value)}
                        accessibilityRole="button"
                        accessibilityState={{ selected: active }}
                        accessibilityLabel={`Categoria: ${c.label}`}
                      >
                        <Text style={[styles.categoryChipText, active && styles.categoryChipTextActive]}>
                          {c.label}
                        </Text>
                      </TouchableOpacity>
                    );
                  })}
                </View>
              </ScrollView>
            </View>
            );
          })}
          {rows.length === 0 ? (
            <Text style={styles.emptyText}>Nenhuma despesa para importar neste extrato.</Text>
          ) : null}

          {/* Entradas (créditos): só informação. Nunca são importadas nem viram despesa. */}
          {creditsCount > 0 ? (
            <View style={styles.creditsCard}>
              <View style={styles.creditsHeader}>
                <Ionicons name="arrow-down-circle-outline" size={16} color={MUTED} />
                <Text style={styles.creditsTitle}>{creditsLabel(creditsCount)}</Text>
              </View>
              <Text style={styles.creditsHint}>
                Entradas não são despesas e ficam de fora da importação.
              </Text>
              {(data?.credits ?? []).map((credit, i) => (
                <View key={`${credit.date}-${i}`} style={styles.creditRow}>
                  <View style={styles.flex}>
                    <Text style={styles.creditDescription} numberOfLines={1}>
                      {credit.description}
                    </Text>
                    <Text style={styles.creditDate}>{formatDate(credit.date)}</Text>
                  </View>
                  <View style={styles.creditBadge}>
                    <Text style={styles.creditBadgeText}>Entrada</Text>
                  </View>
                  <Text style={styles.creditAmount}>{`R$ ${formatBRLInput(Math.round(credit.amount * 100))}`}</Text>
                </View>
              ))}
            </View>
          ) : null}
          <View style={styles.scrollPadding} />
        </ScrollView>

        {/* Confirm button */}
        <View style={styles.footer}>
          <TouchableOpacity
            style={[
              styles.confirmBtn,
              (confirmMutation.isPending || rows.length === 0) && styles.confirmBtnDisabled,
            ]}
            onPress={handleConfirm}
            disabled={confirmMutation.isPending || rows.length === 0}
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
  inputError: { borderColor: ERROR },
  fieldErrorText: { color: ERROR, fontSize: 12, marginTop: -4 },
  amountRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  currencyLabel: { fontSize: 14, color: MUTED, fontWeight: '600' },
  amountInput: { flex: 1 },
  categoryRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  categoryLabel: { fontSize: 13, color: MUTED, fontWeight: '600', minWidth: 72 },
  categoryChip: {
    paddingHorizontal: 12,
    paddingVertical: 6,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: colors.border,
  },
  categoryChipActive: { backgroundColor: PRIMARY, borderColor: PRIMARY },
  categoryChipText: { fontSize: 13, color: MUTED },
  categoryChipTextActive: { color: colors.text, fontWeight: '600' },
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
  emptyText: { color: MUTED, fontSize: 14, textAlign: 'center', paddingVertical: 24 },
  creditsCard: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    borderWidth: 1,
    borderColor: BORDER,
    gap: 8,
    opacity: 0.85,
  },
  creditsHeader: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  creditsTitle: { fontSize: 14, fontWeight: '700', color: TEXT },
  creditsHint: { fontSize: 12, color: MUTED },
  creditRow: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  creditDescription: { fontSize: 13, color: TEXT },
  creditDate: { fontSize: 11, color: MUTED },
  creditBadge: {
    paddingHorizontal: 8,
    paddingVertical: 2,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: SUCCESS,
  },
  creditBadgeText: { fontSize: 11, color: SUCCESS, fontWeight: '600' },
  creditAmount: { fontSize: 13, color: MUTED },
});
