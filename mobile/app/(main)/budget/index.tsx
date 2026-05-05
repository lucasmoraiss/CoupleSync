// Income Sources screen — personal income categories + partner (read-only) + shared
import React, { useState, useCallback } from 'react';
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
  Alert,
} from 'react-native';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { incomeApiClient, isCoupleRequiredError } from '@/services/apiClient';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';
import { useToast } from '@/components/Toast/useToast';
import type { IncomeSourceResponse, IncomeGroupResponse } from '@/types/api';

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

// ─── Income Source Row (inline edit) ──────────────────────────────────────────
function IncomeSourceRow({
  source,
  editable,
  onUpdate,
  onDelete,
  isSaving,
}: {
  source: IncomeSourceResponse;
  editable: boolean;
  onUpdate: (id: string, name: string, amountCents: number) => void;
  onDelete: (id: string) => void;
  isSaving: boolean;
}) {
  const [editMode, setEditMode] = useState(false);
  const [name, setName] = useState(source.name);
  const [amountCents, setAmountCents] = useState(Math.round(source.amount * 100));

  const handleSave = useCallback(() => {
    onUpdate(source.id, name.trim() || source.name, amountCents);
    setEditMode(false);
  }, [source.id, source.name, name, amountCents, onUpdate]);

  const handleCancel = useCallback(() => {
    setName(source.name);
    setAmountCents(Math.round(source.amount * 100));
    setEditMode(false);
  }, [source.name, source.amount]);

  if (!editable) {
    return (
      <View style={styles.sourceRow}>
        <View style={styles.sourceInfo}>
          <Text style={styles.sourceName}>{source.name}</Text>
          <Text style={styles.sourceAmount}>{formatBRL(source.amount)}</Text>
        </View>
        {source.isShared && (
          <View style={styles.sharedBadge}>
            <Ionicons name="people-outline" size={12} color={ACCENT} />
          </View>
        )}
      </View>
    );
  }

  if (editMode) {
    return (
      <View style={styles.sourceRowEdit}>
        <TextInput
          style={styles.editNameInput}
          value={name}
          onChangeText={setName}
          placeholder="Nome"
          placeholderTextColor={MUTED}
          accessibilityLabel="Nome da fonte de renda"
        />
        <View style={styles.editAmountCell}>
          <Text style={styles.currencySymbolSmall}>R$</Text>
          <TextInput
            style={styles.editAmountInput}
            value={amountCents > 0 ? formatBRLInput(amountCents) : ''}
            onChangeText={(raw) => {
              const digits = raw.replace(/[^\d]/g, '');
              setAmountCents(digits ? Number(digits) : 0);
            }}
            placeholder="0,00"
            placeholderTextColor={MUTED}
            keyboardType="numeric"
            accessibilityLabel="Valor"
          />
        </View>
        <TouchableOpacity onPress={handleSave} disabled={isSaving} style={styles.iconBtn} accessibilityLabel="Salvar">
          <Ionicons name="checkmark-circle" size={22} color={SUCCESS} />
        </TouchableOpacity>
        <TouchableOpacity onPress={handleCancel} style={styles.iconBtn} accessibilityLabel="Cancelar">
          <Ionicons name="close-circle" size={22} color={MUTED} />
        </TouchableOpacity>
      </View>
    );
  }

  return (
    <View style={styles.sourceRow}>
      <View style={styles.sourceInfo}>
        <Text style={styles.sourceName}>{source.name}</Text>
        <Text style={styles.sourceAmount}>{formatBRL(source.amount)}</Text>
      </View>
      <View style={styles.rowActions}>
        {source.isShared && (
          <View style={styles.sharedBadge}>
            <Ionicons name="people-outline" size={12} color={ACCENT} />
          </View>
        )}
        <TouchableOpacity onPress={() => setEditMode(true)} style={styles.iconBtn} accessibilityLabel="Editar">
          <Ionicons name="pencil-outline" size={18} color={ACCENT} />
        </TouchableOpacity>
        <TouchableOpacity
          onPress={() => {
            Alert.alert('Remover', `Deseja remover "${source.name}"?`, [
              { text: 'Cancelar', style: 'cancel' },
              { text: 'Remover', style: 'destructive', onPress: () => onDelete(source.id) },
            ]);
          }}
          style={styles.iconBtn}
          accessibilityLabel="Remover"
        >
          <Ionicons name="trash-outline" size={18} color={ERROR} />
        </TouchableOpacity>
      </View>
    </View>
  );
}

// ─── Income Group Section ─────────────────────────────────────────────────────
function IncomeGroupSection({
  title,
  icon,
  group,
  editable,
  onUpdate,
  onDelete,
  isSaving,
}: {
  title: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  group: IncomeGroupResponse;
  editable: boolean;
  onUpdate: (id: string, name: string, amountCents: number) => void;
  onDelete: (id: string) => void;
  isSaving: boolean;
}) {
  if (group.sources.length === 0 && !editable) return null;

  return (
    <View style={styles.groupSection}>
      <View style={styles.groupHeader}>
        <Ionicons name={icon} size={18} color={ACCENT} />
        <Text style={styles.groupTitle}>{title}</Text>
        {group.userName && <Text style={styles.groupUserName}>({group.userName})</Text>}
      </View>

      {group.sources.map((source) => (
        <IncomeSourceRow
          key={source.id}
          source={source}
          editable={editable}
          onUpdate={onUpdate}
          onDelete={onDelete}
          isSaving={isSaving}
        />
      ))}

      {group.sources.length === 0 && (
        <Text style={styles.emptyText}>Nenhuma fonte de renda cadastrada</Text>
      )}

      <View style={styles.groupTotalRow}>
        <Text style={styles.groupTotalLabel}>Subtotal</Text>
        <Text style={styles.groupTotalValue}>{formatBRL(group.total)}</Text>
      </View>
    </View>
  );
}

// ─── Add Income Source Form ───────────────────────────────────────────────────
function AddIncomeForm({
  month,
  onCreated,
}: {
  month: string;
  onCreated: () => void;
}) {
  const { toast } = useToast();
  const [name, setName] = useState('');
  const [amountCents, setAmountCents] = useState(0);
  const [isShared, setIsShared] = useState(false);
  const [expanded, setExpanded] = useState(false);

  const createMutation = useMutation({
    mutationFn: () =>
      incomeApiClient.create({
        month,
        name: name.trim(),
        amount: amountCents / 100,
        currency: 'BRL',
        isShared,
      }),
    onSuccess: () => {
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      setName('');
      setAmountCents(0);
      setIsShared(false);
      setExpanded(false);
      onCreated();
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error('Não foi possível criar a fonte de renda.');
    },
  });

  if (!expanded) {
    return (
      <TouchableOpacity style={styles.addBtn} onPress={() => setExpanded(true)} accessibilityLabel="Adicionar fonte de renda">
        <Ionicons name="add-circle-outline" size={20} color={ACCENT} />
        <Text style={styles.addBtnText}>Adicionar fonte de renda</Text>
      </TouchableOpacity>
    );
  }

  return (
    <View style={styles.addForm}>
      <TextInput
        style={styles.addInput}
        value={name}
        onChangeText={setName}
        placeholder="Ex: Salário, Freelance, Investimentos"
        placeholderTextColor={MUTED}
        accessibilityLabel="Nome da fonte de renda"
        autoFocus
      />
      <View style={styles.addAmountRow}>
        <Text style={styles.currencySymbol}>R$</Text>
        <TextInput
          style={styles.addAmountInput}
          value={amountCents > 0 ? formatBRLInput(amountCents) : ''}
          onChangeText={(raw) => {
            const digits = raw.replace(/[^\d]/g, '');
            setAmountCents(digits ? Number(digits) : 0);
          }}
          placeholder="0,00"
          placeholderTextColor={MUTED}
          keyboardType="numeric"
          accessibilityLabel="Valor mensal"
        />
      </View>
      <TouchableOpacity
        style={styles.sharedToggle}
        onPress={() => setIsShared(!isShared)}
        accessibilityLabel={isShared ? 'Desmarcar como compartilhado' : 'Marcar como compartilhado'}
      >
        <Ionicons name={isShared ? 'checkbox' : 'square-outline'} size={20} color={ACCENT} />
        <Text style={styles.sharedToggleText}>Renda compartilhada (editável por ambos)</Text>
      </TouchableOpacity>
      <View style={styles.addFormActions}>
        <TouchableOpacity
          style={[styles.addSaveBtn, createMutation.isPending && styles.btnDisabled]}
          onPress={() => createMutation.mutate()}
          disabled={createMutation.isPending || !name.trim()}
          accessibilityLabel="Salvar fonte de renda"
        >
          {createMutation.isPending ? (
            <ActivityIndicator size="small" color={TEXT} />
          ) : (
            <Text style={styles.addSaveBtnText}>Adicionar</Text>
          )}
        </TouchableOpacity>
        <TouchableOpacity
          style={styles.addCancelBtn}
          onPress={() => { setExpanded(false); setName(''); setAmountCents(0); setIsShared(false); }}
          accessibilityLabel="Cancelar"
        >
          <Text style={styles.addCancelBtnText}>Cancelar</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
}

// ─── Main Screen ──────────────────────────────────────────────────────────────
export default function IncomeSourcesScreen() {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const [month] = useState(currentMonthISO());

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['incomes', 'current'],
    queryFn: () => incomeApiClient.getCurrent().then((r) => r.data),
    retry: false,
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, name, amountCents }: { id: string; name: string; amountCents: number }) =>
      incomeApiClient.update(id, { name, amount: amountCents / 100 }),
    onSuccess: () => {
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      queryClient.invalidateQueries({ queryKey: ['incomes'] });
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error('Não foi possível atualizar.');
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => incomeApiClient.delete(id),
    onSuccess: () => {
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      queryClient.invalidateQueries({ queryKey: ['incomes'] });
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error('Não foi possível remover.');
    },
  });

  const handleUpdate = useCallback(
    (id: string, name: string, amountCents: number) => {
      updateMutation.mutate({ id, name, amountCents });
    },
    [updateMutation],
  );

  const handleDelete = useCallback(
    (id: string) => {
      deleteMutation.mutate(id);
    },
    [deleteMutation],
  );

  const handleCreated = useCallback(() => {
    queryClient.invalidateQueries({ queryKey: ['incomes'] });
  }, [queryClient]);

  const isSaving = updateMutation.isPending || deleteMutation.isPending;

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

  const emptyState = !data || (
    data.personalIncome.sources.length === 0 &&
    (!data.partnerIncome || data.partnerIncome.sources.length === 0) &&
    data.sharedIncome.sources.length === 0
  );

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
          <Text style={styles.screenTitle}>Fontes de Renda</Text>

          {/* Month display */}
          <View style={styles.card}>
            <Text style={styles.cardLabel}>Mês</Text>
            <Text style={styles.monthText}>{formatMonthDisplay(month)}</Text>
          </View>

          {/* Couple Total Card */}
          {data && (
            <View style={styles.totalCard}>
              <View>
                <Text style={styles.totalLabel}>Renda Total do Casal</Text>
                <Text style={styles.totalHint}>Soma de todas as fontes de renda</Text>
              </View>
              <Text style={styles.totalValue}>{formatBRL(data.coupleTotal)}</Text>
            </View>
          )}

          {/* Personal Income */}
          {data && (
            <IncomeGroupSection
              title="Minhas Rendas"
              icon="person-outline"
              group={data.personalIncome}
              editable={true}
              onUpdate={handleUpdate}
              onDelete={handleDelete}
              isSaving={isSaving}
            />
          )}

          {/* Add new source */}
          <AddIncomeForm month={month} onCreated={handleCreated} />

          {/* Partner Income (read-only) */}
          {data?.partnerIncome && data.partnerIncome.sources.length > 0 && (
            <IncomeGroupSection
              title="Rendas do Parceiro"
              icon="heart-outline"
              group={data.partnerIncome}
              editable={false}
              onUpdate={handleUpdate}
              onDelete={handleDelete}
              isSaving={isSaving}
            />
          )}

          {/* Shared Income */}
          {data && data.sharedIncome.sources.length > 0 && (
            <IncomeGroupSection
              title="Rendas Compartilhadas"
              icon="people-outline"
              group={data.sharedIncome}
              editable={true}
              onUpdate={handleUpdate}
              onDelete={handleDelete}
              isSaving={isSaving}
            />
          )}

          {/* Empty state hint */}
          {emptyState && (
            <View style={styles.emptyCard}>
              <Ionicons name="wallet-outline" size={40} color={MUTED} />
              <Text style={styles.emptyTitle}>Configure suas rendas</Text>
              <Text style={styles.emptyDescription}>
                Adicione suas fontes de renda (salário, freelance, investimentos, etc.) para ter uma visão completa do orçamento do casal.
              </Text>
            </View>
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
  totalCard: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 16,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: ACCENT,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  totalLabel: { fontSize: 14, color: TEXT, fontWeight: '700' },
  totalHint: { fontSize: 11, color: MUTED, marginTop: 2 },
  totalValue: { fontSize: 22, fontWeight: '700', color: SUCCESS },
  groupSection: { marginBottom: 16 },
  groupHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: 8,
    gap: 6,
  },
  groupTitle: {
    fontSize: 14,
    color: MUTED,
    fontWeight: '700',
    textTransform: 'uppercase',
    letterSpacing: 0.5,
  },
  groupUserName: { fontSize: 12, color: MUTED },
  sourceRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    backgroundColor: CARD,
    borderRadius: 10,
    padding: 12,
    marginBottom: 6,
    borderWidth: 1,
    borderColor: BORDER,
  },
  sourceRowEdit: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: CARD,
    borderRadius: 10,
    padding: 8,
    marginBottom: 6,
    borderWidth: 1,
    borderColor: ACCENT,
  },
  sourceInfo: { flex: 1 },
  sourceName: { fontSize: 14, color: TEXT, fontWeight: '600' },
  sourceAmount: { fontSize: 15, color: ACCENT, fontWeight: '700', marginTop: 2 },
  rowActions: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  sharedBadge: { paddingHorizontal: 4 },
  iconBtn: { padding: 4 },
  editNameInput: { flex: 2, fontSize: 14, color: TEXT, marginRight: 6, padding: 4 },
  editAmountCell: { flex: 1.5, flexDirection: 'row', alignItems: 'center' },
  editAmountInput: { flex: 1, fontSize: 14, color: TEXT, padding: 0 },
  currencySymbolSmall: { fontSize: 14, color: ACCENT, marginRight: 2, fontWeight: '600' },
  groupTotalRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    paddingHorizontal: 12,
    paddingTop: 6,
  },
  groupTotalLabel: { fontSize: 13, color: MUTED, fontWeight: '600' },
  groupTotalValue: { fontSize: 14, color: TEXT, fontWeight: '700' },
  emptyText: { fontSize: 13, color: MUTED, textAlign: 'center', paddingVertical: 12 },
  addBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    padding: 10,
    marginBottom: 12,
  },
  addBtnText: { color: ACCENT, fontSize: 14, fontWeight: '600', marginLeft: 6 },
  addForm: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 14,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: ACCENT,
  },
  addInput: { fontSize: 15, color: TEXT, marginBottom: 10, borderBottomWidth: 1, borderBottomColor: BORDER, paddingBottom: 8 },
  addAmountRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 10 },
  currencySymbol: { fontSize: 18, color: ACCENT, marginRight: 8, fontWeight: '700' },
  addAmountInput: { flex: 1, fontSize: 18, color: TEXT, padding: 0 },
  sharedToggle: { flexDirection: 'row', alignItems: 'center', gap: 8, marginBottom: 12 },
  sharedToggleText: { fontSize: 13, color: MUTED },
  addFormActions: { flexDirection: 'row', gap: 10 },
  addSaveBtn: {
    flex: 1,
    backgroundColor: PRIMARY,
    borderRadius: 10,
    padding: 12,
    alignItems: 'center',
  },
  addSaveBtnText: { color: TEXT, fontSize: 14, fontWeight: '700' },
  addCancelBtn: {
    flex: 1,
    borderRadius: 10,
    padding: 12,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: BORDER,
  },
  addCancelBtnText: { color: MUTED, fontSize: 14, fontWeight: '600' },
  btnDisabled: { opacity: 0.6 },
  emptyCard: {
    backgroundColor: CARD,
    borderRadius: 12,
    padding: 24,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: BORDER,
    marginBottom: 12,
  },
  emptyTitle: { fontSize: 16, color: TEXT, fontWeight: '700', marginTop: 12 },
  emptyDescription: { fontSize: 13, color: MUTED, textAlign: 'center', marginTop: 8, lineHeight: 18 },
  bottomSpacer: { height: 40 },
});
