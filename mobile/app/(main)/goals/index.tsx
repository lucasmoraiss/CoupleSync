// AC-005: Goals management screen — list, create, edit, delete
import { getApiErrorMessage } from '@/services/apiError';
import React, { useState, useCallback } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  FlatList,
  TouchableOpacity,
  ActivityIndicator,
  RefreshControl,
  Modal,
  Pressable,
  ScrollView,
  TextInput,
  Alert,
  KeyboardAvoidingView,
  Platform,
} from 'react-native';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { goalsApiClient, isCoupleRequiredError } from '@/services/apiClient';
import type { GoalDto, GetGoalsResponse } from '@/types/api';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { EmptyState } from '@/components/EmptyState';
import { ErrorState } from '@/components/ErrorState';
import { useToast } from '@/components/Toast/useToast';
import { amountCentsError, centsFromDigits } from '@/utils/amount';
import { isDeadlineBeforeToday } from '@/utils/goalDeadline';
import { describeProgress, spokenBRL } from '@/utils/a11y';

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
const OVERLAY = colors.overlay;

// ─── Helpers ──────────────────────────────────────────────────────────────────
function formatBRL(amount: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(amount);
}

function formatDeadline(dateStr: string): string {
  const d = new Date(dateStr);
  return d.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

/** Parse DD/MM/YYYY → ISO string (UTC noon) for API, returns null if invalid */
function parseDateInput(value: string): string | null {
  const match = value.match(/^(\d{2})\/(\d{2})\/(\d{4})$/);
  if (!match) return null;
  const [, day, month, year] = match;
  const d = new Date(Date.UTC(Number(year), Number(month) - 1, Number(day), 12, 0, 0));
  if (
    d.getUTCFullYear() !== Number(year) ||
    d.getUTCMonth() !== Number(month) - 1 ||
    d.getUTCDate() !== Number(day)
  ) {
    return null;
  }
  return d.toISOString();
}

/** Format ISO date to DD/MM/YYYY for text input */
function toInputDate(dateStr: string): string {
  const d = new Date(dateStr);
  const dd = String(d.getDate()).padStart(2, '0');
  const mm = String(d.getMonth() + 1).padStart(2, '0');
  const yyyy = d.getFullYear();
  return `${dd}/${mm}/${yyyy}`;
}

/** Format raw amount string to BRL display (numbers only, 2-decimal) */
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

function isDeadlinePast(dateStr: string): boolean {
  return isDeadlineBeforeToday(dateStr);
}

// ─── Goal form state ──────────────────────────────────────────────────────────
interface GoalFormState {
  title: string;
  description: string;
  amountCents: number; // stored as cents for input control
  manualAmountCents: number; // manually saved amount in cents (the transactions part is read-only)
  linkedAmount: number; // sum of linked transactions, shown for context only
  deadlineInput: string; // DD/MM/YYYY
}

const EMPTY_FORM: GoalFormState = {
  title: '',
  description: '',
  amountCents: 0,
  manualAmountCents: 0,
  linkedAmount: 0,
  deadlineInput: '',
};

/** Valor guardado manualmente (servidor antigo não manda o campo: currentAmount era só o manual). */
function manualAmountOf(goal: GoalDto): number {
  return goal.manualAmount ?? goal.currentAmount;
}

function linkedAmountOf(goal: GoalDto): number {
  return goal.linkedAmount ?? 0;
}

function formFromGoal(goal: GoalDto): GoalFormState {
  return {
    title: goal.title,
    description: goal.description ?? '',
    amountCents: Math.round(goal.targetAmount * 100),
    manualAmountCents: Math.round(manualAmountOf(goal) * 100),
    linkedAmount: linkedAmountOf(goal),
    deadlineInput: toInputDate(goal.deadline),
  };
}

// ─── Goal form modal ──────────────────────────────────────────────────────────
interface GoalFormModalProps {
  visible: boolean;
  mode: 'create' | 'edit';
  initialValues: GoalFormState;
  isSaving: boolean;
  onClose: () => void;
  onSave: (form: GoalFormState) => void;
}

function GoalFormModal({
  visible,
  mode,
  initialValues,
  isSaving,
  onClose,
  onSave,
}: GoalFormModalProps) {
  const [form, setForm] = useState<GoalFormState>(initialValues);

  // Reset form when modal opens with new initial values
  React.useEffect(() => {
    if (visible) setForm(initialValues);
  }, [visible, initialValues]);

  const handleAmountChange = (raw: string) => {
    setForm((f) => ({ ...f, amountCents: centsFromDigits(raw) }));
  };

  const handleDeadlineChange = (raw: string) => {
    // Auto-insert slashes for DD/MM/YYYY pattern
    let v = raw.replace(/[^\d]/g, '');
    if (v.length > 2) v = v.slice(0, 2) + '/' + v.slice(2);
    if (v.length > 5) v = v.slice(0, 5) + '/' + v.slice(5);
    v = v.slice(0, 10);
    setForm((f) => ({ ...f, deadlineInput: v }));
  };

  const titleError = !form.title.trim() ? 'Título é obrigatório' : null;
  const amountError = amountCentsError(form.amountCents);
  const manualAmountError = amountCentsError(form.manualAmountCents, { allowZero: true });
  const deadlineError = (() => {
    const iso = parseDateInput(form.deadlineInput);
    if (!iso) return 'Data inválida (DD/MM/AAAA)';
    // "Hoje" é o dia de Brasília, o mesmo que o servidor usa.
    if (isDeadlineBeforeToday(iso)) return 'O prazo deve ser hoje ou uma data futura';
    return null;
  })();
  const isValid = !titleError && !amountError && !manualAmountError && !deadlineError;

  return (
    <Modal
      visible={visible}
      transparent
      animationType="slide"
      onRequestClose={onClose}
    >
      <KeyboardAvoidingView
        style={styles.modalOverlay}
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
      >
        <Pressable accessibilityRole="button" accessibilityLabel="Fechar" style={styles.modalBackdrop} onPress={onClose} />
        <View style={styles.modalSheet}>
          {/* Handle */}
          <View style={styles.modalHandle} />

          {/* Header */}
          <View style={styles.modalHeader}>
            <Text style={styles.modalTitle} accessibilityRole="header">
              {mode === 'create' ? 'Nova meta' : 'Editar meta'}
            </Text>
            <TouchableOpacity style={{ minHeight: 44, minWidth: 44, alignItems: 'center', justifyContent: 'center' }} accessibilityRole="button" onPress={onClose} accessibilityLabel="Fechar" disabled={isSaving}>
              <Ionicons name="close" size={24} color={MUTED} />
            </TouchableOpacity>
          </View>

          <ScrollView showsVerticalScrollIndicator={false} keyboardShouldPersistTaps="handled">
            {/* Title */}
            <Text style={styles.fieldLabel}>Título *</Text>
            <TextInput
              style={styles.input}
              value={form.title}
              onChangeText={(v) => setForm((f) => ({ ...f, title: v }))}
              placeholder="Ex: Viagem para Europa"
              placeholderTextColor={MUTED}
              maxLength={100}
              editable={!isSaving}
              accessibilityLabel="Título da meta"
            />

            {/* Description */}
            <Text style={styles.fieldLabel}>Descrição (opcional)</Text>
            <TextInput
              style={[styles.input, styles.inputMultiline]}
              value={form.description}
              onChangeText={(v) => setForm((f) => ({ ...f, description: v }))}
              placeholder="Descreva a meta..."
              placeholderTextColor={MUTED}
              multiline
              numberOfLines={3}
              maxLength={500}
              editable={!isSaving}
              accessibilityLabel="Descrição da meta"
            />

            {/* Target amount */}
            <Text style={styles.fieldLabel}>Valor alvo (R$) *</Text>
            <TextInput
              style={styles.input}
              value={form.amountCents > 0 ? formatBRLInput(form.amountCents) : ''}
              onChangeText={handleAmountChange}
              placeholder="0,00"
              placeholderTextColor={MUTED}
              keyboardType="numeric"
              editable={!isSaving}
              accessibilityLabel="Valor alvo da meta"
            />
            {form.title.trim().length > 0 && amountError && (
              <Text style={styles.fieldError}>{amountError}</Text>
            )}

            {/* Manually saved amount (the progress also counts linked transactions) */}
            {mode === 'edit' && (
              <>
                <Text style={styles.fieldLabel}>Guardado manualmente (R$)</Text>
                <TextInput
                  style={styles.input}
                  value={form.manualAmountCents > 0 ? formatBRLInput(form.manualAmountCents) : ''}
                  onChangeText={(raw) => {
                    setForm((f) => ({ ...f, manualAmountCents: centsFromDigits(raw) }));
                  }}
                  placeholder="0,00"
                  placeholderTextColor={MUTED}
                  keyboardType="numeric"
                  editable={!isSaving}
                  accessibilityLabel="Valor guardado manualmente na meta"
                />
                {form.linkedAmount > 0 && (
                  <Text
                    style={styles.fieldHint}
                    accessibilityLabel={`Mais ${spokenBRL(form.linkedAmount)} de transações vinculadas, somados automaticamente.`}
                  >
                    Mais {formatBRL(form.linkedAmount)} de transações vinculadas, somados automaticamente.
                  </Text>
                )}
              </>
            )}

            {/* Deadline */}
            <Text style={styles.fieldLabel}>Prazo *</Text>
            <TextInput
              style={styles.input}
              value={form.deadlineInput}
              onChangeText={handleDeadlineChange}
              placeholder="DD/MM/AAAA"
              placeholderTextColor={MUTED}
              keyboardType="numeric"
              maxLength={10}
              editable={!isSaving}
              accessibilityLabel="Prazo da meta"
            />
            {form.deadlineInput.length === 10 && deadlineError && (
              <Text style={styles.fieldError}>{deadlineError}</Text>
            )}

            {/* Save button */}
            <TouchableOpacity accessibilityRole="button"
              style={[styles.saveBtn, (!isValid || isSaving) && styles.saveBtnDisabled]}
              onPress={() => isValid && !isSaving && onSave(form)}
              disabled={!isValid || isSaving}
              accessibilityLabel={mode === 'create' ? 'Criar meta' : 'Salvar alterações'}
            >
              {isSaving ? (
                <ActivityIndicator size="small" color={TEXT} />
              ) : (
                <Text style={styles.saveBtnText}>
                  {mode === 'create' ? 'Criar meta' : 'Salvar alterações'}
                </Text>
              )}
            </TouchableOpacity>
          </ScrollView>
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

// ─── Goal card ────────────────────────────────────────────────────────────────
interface GoalCardProps {
  goal: GoalDto;
  onEdit: (goal: GoalDto) => void;
  onDelete: (goal: GoalDto) => void;
}

function GoalCard({ goal, onEdit, onDelete }: GoalCardProps) {
  const isPast = isDeadlinePast(goal.deadline);
  // Tudo vem do servidor (mesma conta em todas as telas); o cálculo local só cobre servidor antigo.
  const progressPercent = goal.progressPercent ?? (goal.targetAmount > 0
    ? Math.min(100, (goal.currentAmount / goal.targetAmount) * 100)
    : 0);
  const isAchieved = goal.isAchieved ?? goal.currentAmount >= goal.targetAmount;
  const manualAmount = manualAmountOf(goal);
  const linkedAmount = linkedAmountOf(goal);

  return (
    <View style={styles.card} accessibilityLabel={`Meta: ${goal.title}`}>
      {/* Top row: title + achieved badge */}
      <View style={styles.cardHeader}>
        <View style={styles.cardTitleWrap}>
          <Ionicons name="flag-outline" size={18} color={ACCENT} style={styles.cardIcon} />
          <Text style={styles.cardTitle} numberOfLines={1}>
            {goal.title}
          </Text>
        </View>
        {isAchieved && (
          <View style={[styles.badge, styles.badgeAchieved]}>
            <Text style={[styles.badgeText, styles.badgeTextAchieved]}>Meta atingida 🎯</Text>
          </View>
        )}
      </View>

      {/* Description */}
      {goal.description ? (
        <Text style={styles.cardDescription} numberOfLines={2}>
          {goal.description}
        </Text>
      ) : null}

      {/* Progress bar */}
      <View
        style={styles.progressBarBg}
        accessible
        accessibilityRole="progressbar"
        accessibilityLabel={describeProgress(goal.title, progressPercent, goal.currentAmount, goal.targetAmount)}
        accessibilityValue={{ min: 0, max: 100, now: Math.round(Math.min(100, progressPercent)) }}
      >
        <View style={[styles.progressBarFill, { width: `${progressPercent}%` as any }]} />
      </View>
      <Text style={styles.progressText} importantForAccessibility="no-hide-descendants" accessibilityElementsHidden>
        {formatBRL(goal.currentAmount)} / {formatBRL(goal.targetAmount)} ({Math.floor(progressPercent)}%)
      </Text>
      <Text
        style={styles.progressBreakdown}
        accessibilityLabel={`Guardado manualmente: ${spokenBRL(manualAmount)}. Por transações: ${spokenBRL(linkedAmount)}.`}
      >
        Guardado manualmente: {formatBRL(manualAmount)} · Por transações: {formatBRL(linkedAmount)}
      </Text>

      {/* Amount + deadline */}
      <View style={styles.cardRow}>
        <View style={styles.cardInfoItem}>
          <Ionicons name="calendar-outline" size={14} color={isPast ? ERROR : MUTED} />
          <Text style={[styles.cardInfoLabel, isPast && styles.textError]}>Prazo</Text>
          <Text style={[styles.cardInfoValue, isPast && styles.textError]}>
            {formatDeadline(goal.deadline)}
          </Text>
        </View>
      </View>

      {/* Actions */}
      <View style={styles.cardActions}>
        <TouchableOpacity accessibilityRole="button"
          style={styles.cardActionBtn}
          onPress={() => onEdit(goal)}
          accessibilityLabel={`Editar meta ${goal.title}`}
        >
          <Ionicons name="pencil-outline" size={16} color={ACCENT} />
          <Text style={styles.cardActionText}>Editar</Text>
        </TouchableOpacity>
        <TouchableOpacity accessibilityRole="button"
          style={[styles.cardActionBtn, styles.cardActionBtnDanger]}
          onPress={() => onDelete(goal)}
          accessibilityLabel={`Excluir meta ${goal.title}`}
        >
          <Ionicons name="trash-outline" size={16} color={ERROR} />
          <Text style={[styles.cardActionText, styles.textError]}>Excluir</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
}

// ─── Main screen ──────────────────────────────────────────────────────────────
export default function GoalsScreen() {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const [refreshing, setRefreshing] = useState(false);

  // Modal state
  const [createModalVisible, setCreateModalVisible] = useState(false);
  const [editModalVisible, setEditModalVisible] = useState(false);
  const [editingGoal, setEditingGoal] = useState<GoalDto | null>(null);

  const { data, isLoading, isError, error: loadError, refetch } = useQuery<GetGoalsResponse>({
    queryKey: ['goals'],
    queryFn: async () => {
      const res = await goalsApiClient.list(false);
      return res.data;
    },
  });

  const goals = (data?.items ?? []).filter((g) => g.status === 'Active');

  const handleRefresh = async () => {
    setRefreshing(true);
    await refetch();
    setRefreshing(false);
  };

  // Create mutation
  const { mutate: createGoal, isPending: isCreating } = useMutation({
    mutationFn: (form: GoalFormState) => {
      const isoDeadline = parseDateInput(form.deadlineInput)!;
      return goalsApiClient.create({
        title: form.title.trim(),
        description: form.description.trim() || undefined,
        targetAmount: form.amountCents / 100,
        currency: 'BRL',
        deadline: isoDeadline,
      });
    },
    onSuccess: () => {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
      queryClient.invalidateQueries({ queryKey: ['goals'] });
      setCreateModalVisible(false);
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error(getApiErrorMessage(error, 'Não foi possível criar a meta. Tente novamente.'));
    },
  });

  // Update mutation
  const { mutate: updateGoal, isPending: isUpdating } = useMutation({
    mutationFn: ({ id, form }: { id: string; form: GoalFormState }) => {
      const isoDeadline = parseDateInput(form.deadlineInput)!;
      return goalsApiClient.update(id, {
        title: form.title.trim(),
        description: form.description.trim() || undefined,
        targetAmount: form.amountCents / 100,
        manualAmount: form.manualAmountCents / 100,
        deadline: isoDeadline,
      });
    },
    onSuccess: () => {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
      queryClient.invalidateQueries({ queryKey: ['goals'] });
      setEditModalVisible(false);
      setEditingGoal(null);
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error(getApiErrorMessage(error, 'Não foi possível atualizar a meta. Tente novamente.'));
    },
  });

  // Delete mutation
  const { mutate: deleteGoal } = useMutation({
    mutationFn: (id: string) => goalsApiClient.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['goals'] });
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error(getApiErrorMessage(error, 'Não foi possível excluir a meta. Tente novamente.'));
    },
  });

  const handleOpenEdit = useCallback((goal: GoalDto) => {
    setEditingGoal(goal);
    setEditModalVisible(true);
  }, []);

  const handleDelete = useCallback(
    (goal: GoalDto) => {
      Alert.alert(
        'Excluir meta',
        `Tem certeza que deseja excluir "${goal.title}"? Esta ação não pode ser desfeita.`,
        [
          { text: 'Cancelar', style: 'cancel' },
          {
            text: 'Excluir',
            style: 'destructive',
            onPress: () => deleteGoal(goal.id),
          },
        ]
      );
    },
    [deleteGoal]
  );

  const renderGoal = useCallback(
    ({ item }: { item: GoalDto }) => (
      <GoalCard goal={item} onEdit={handleOpenEdit} onDelete={handleDelete} />
    ),
    [handleOpenEdit, handleDelete]
  );

  return (
    <SafeAreaView style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <View>
          <Text style={styles.title} accessibilityRole="header">Metas</Text>
          <Text style={styles.subtitle}>Objetivos financeiros do casal</Text>
        </View>
        <TouchableOpacity accessibilityRole="button"
          style={styles.addBtn}
          onPress={() => setCreateModalVisible(true)}
          accessibilityLabel="Criar nova meta"
        >
          <Ionicons name="add" size={24} color={TEXT} />
        </TouchableOpacity>
      </View>

      {/* Loading */}
      {isLoading && <LoadingState />}

      {/* Error */}
      {isError && !isLoading && (
        <ErrorState
          message={getApiErrorMessage(loadError, 'Não foi possível carregar as metas.')}
          onRetry={refetch}
        />
      )}

      {/* List */}
      {!isLoading && !isError && (
        <FlatList
          data={goals}
          keyExtractor={(item) => item.id}
          renderItem={renderGoal}
          contentContainerStyle={goals.length === 0 ? styles.listEmpty : styles.listContent}
          showsVerticalScrollIndicator={false}
          refreshControl={
            <RefreshControl refreshing={refreshing} onRefresh={handleRefresh} tintColor={ACCENT} />
          }
          ListEmptyComponent={
            <EmptyState
              icon="flag-outline"
              title="Nenhuma meta ativa"
              subtitle="Crie uma meta para acompanhar o progresso juntos"
              ctaLabel="Nova meta"
              onCtaPress={() => setCreateModalVisible(true)}
            />
          }
        />
      )}

      {/* Create modal */}
      <GoalFormModal
        visible={createModalVisible}
        mode="create"
        initialValues={EMPTY_FORM}
        isSaving={isCreating}
        onClose={() => setCreateModalVisible(false)}
        onSave={(form) => createGoal(form)}
      />

      {/* Edit modal */}
      <GoalFormModal
        visible={editModalVisible}
        mode="edit"
        initialValues={editingGoal ? formFromGoal(editingGoal) : EMPTY_FORM}
        isSaving={isUpdating}
        onClose={() => {
          setEditModalVisible(false);
          setEditingGoal(null);
        }}
        onSave={(form) => editingGoal && updateGoal({ id: editingGoal.id, form })}
      />
    </SafeAreaView>
  );
}

// ─── Styles ───────────────────────────────────────────────────────────────────
const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: BG },

  // Header
  header: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingHorizontal: 20,
    paddingTop: 24,
    paddingBottom: 16,
  },
  title: { fontSize: 26, fontWeight: '700', color: TEXT },
  subtitle: { fontSize: 14, color: MUTED, marginTop: 2 },
  addBtn: {
    width: 48,
    height: 48,
    borderRadius: 24,
    backgroundColor: PRIMARY,
    alignItems: 'center',
    justifyContent: 'center',
  },

  // Tabs
  tabs: {
    flexDirection: 'row',
    marginHorizontal: 20,
    marginBottom: 16,
    backgroundColor: CARD,
    borderRadius: 10,
    padding: 4,
  },
  tab: {
    flex: 1,
    paddingVertical: 8,
    alignItems: 'center',
    borderRadius: 8,
  },
  tabActive: { backgroundColor: PRIMARY },
  tabText: { fontSize: 14, fontWeight: '500', color: MUTED },
  tabTextActive: { color: TEXT, fontWeight: '600' },

  // List
  listContent: { paddingHorizontal: 20, paddingBottom: 100 },
  listEmpty: { flex: 1, justifyContent: 'center' },

  // Goal card
  card: {
    backgroundColor: CARD,
    borderRadius: 14,
    padding: 16,
    marginBottom: 12,
  },
  cardHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: 8,
  },
  cardTitleWrap: { flexDirection: 'row', alignItems: 'center', flex: 1, marginRight: 8 },
  cardIcon: { marginRight: 8 },
  cardTitle: { fontSize: 16, fontWeight: '600', color: TEXT, flex: 1 },
  cardDescription: { fontSize: 13, color: MUTED, marginBottom: 12, lineHeight: 18 },

  // Progress bar
  progressBarBg: { height: 6, backgroundColor: BORDER, borderRadius: 3, marginBottom: 4 },
  progressBarFill: { height: 6, backgroundColor: SUCCESS, borderRadius: 3 },
  progressText: { fontSize: 12, color: MUTED, marginBottom: 4 },
  progressBreakdown: { fontSize: 11, color: MUTED, marginBottom: 12 },

  // Status badge
  badge: { paddingHorizontal: 8, paddingVertical: 3, borderRadius: 99 },
  badgeActive: { backgroundColor: 'rgba(99,102,241,0.15)' },
  badgeAchieved: { backgroundColor: 'rgba(34,197,94,0.15)' },
  badgeArchived: { backgroundColor: 'rgba(148,163,184,0.15)' },
  badgeText: { fontSize: 11, fontWeight: '600' },
  badgeTextActive: { color: ACCENT },
  badgeTextAchieved: { color: SUCCESS },
  badgeTextArchived: { color: MUTED },

  // Card info row
  cardRow: { flexDirection: 'row', gap: 16, marginBottom: 12 },
  cardInfoItem: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  cardInfoLabel: { fontSize: 12, color: MUTED, marginRight: 2 },
  cardInfoValue: { fontSize: 13, fontWeight: '600', color: TEXT },

  // Card actions
  cardActions: {
    flexDirection: 'row',
    gap: 8,
    borderTopWidth: 1,
    borderTopColor: BORDER,
    paddingTop: 12,
  },
  cardActionBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
    paddingVertical: 6,
    paddingHorizontal: 12,
    borderRadius: 8,
    backgroundColor: 'rgba(129,140,248,0.1)',
    minHeight: 48,
  },
  cardActionBtnDanger: { backgroundColor: 'rgba(239,68,68,0.1)' },
  cardActionText: { fontSize: 13, fontWeight: '500', color: ACCENT },

  // States
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center', paddingBottom: 100 },
  loadingText: { color: MUTED, marginTop: 8 },
  errorCard: {
    margin: 20,
    backgroundColor: CARD,
    borderRadius: 14,
    padding: 20,
    alignItems: 'center',
    gap: 8,
  },
  errorTitle: { fontSize: 15, fontWeight: '600', color: TEXT, textAlign: 'center' },
  errorHint: { fontSize: 13, color: MUTED, textAlign: 'center' },
  emptyWrap: { alignItems: 'center', justifyContent: 'center', paddingVertical: 60 },
  emptyTitle: { fontSize: 18, fontWeight: '700', color: TEXT, marginTop: 16, marginBottom: 6 },
  emptyHint: {
    fontSize: 14,
    color: MUTED,
    textAlign: 'center',
    paddingHorizontal: 40,
    lineHeight: 20,
  },

  textError: { color: ERROR },

  // Modal
  modalOverlay: { flex: 1, justifyContent: 'flex-end' },
  modalBackdrop: { ...StyleSheet.absoluteFillObject, backgroundColor: OVERLAY },
  modalSheet: {
    backgroundColor: CARD,
    borderTopLeftRadius: 20,
    borderTopRightRadius: 20,
    paddingHorizontal: 24,
    paddingBottom: 40,
    maxHeight: '90%',
  },
  modalHandle: {
    width: 40,
    height: 4,
    backgroundColor: BORDER,
    borderRadius: 2,
    alignSelf: 'center',
    marginTop: 12,
    marginBottom: 8,
  },
  modalHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingVertical: 12,
    marginBottom: 8,
  },
  modalTitle: { fontSize: 18, fontWeight: '700', color: TEXT },

  // Form fields
  fieldLabel: { fontSize: 13, fontWeight: '500', color: MUTED, marginBottom: 6, marginTop: 12 },
  input: {
    backgroundColor: BG,
    borderWidth: 1,
    borderColor: BORDER,
    borderRadius: 10,
    paddingHorizontal: 14,
    paddingVertical: 12,
    color: TEXT,
    fontSize: 15,
  },
  inputMultiline: { minHeight: 80, textAlignVertical: 'top' },
  fieldError: { fontSize: 12, color: ERROR, marginTop: 4 },
  fieldHint: { fontSize: 12, color: MUTED, marginTop: 4 },
  saveBtn: {
    backgroundColor: PRIMARY,
    borderRadius: 12,
    paddingVertical: 14,
    alignItems: 'center',
    marginTop: 24,
    marginBottom: 8,
  },
  saveBtnDisabled: { opacity: 0.5 },
  saveBtnText: { color: TEXT, fontSize: 16, fontWeight: '600' },
});

