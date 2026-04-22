// AC-003: Transactions list screen — FlatList + pull-to-refresh + inline category editor
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
} from 'react-native';
import { router } from 'expo-router';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import { transactionsApiClient, isCoupleRequiredError } from '@/services/apiClient';
import {
  PREDEFINED_CATEGORIES,
  getCategoryLabel,
  getCategoryIcon,
} from '@/modules/transactions/categories';
import type { TransactionResponse, GetTransactionsResponse } from '@/types/api';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { EmptyState } from '@/components/EmptyState';
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
const OVERLAY = colors.overlay;

// ─── Helpers ──────────────────────────────────────────────────────────────────
function formatBRL(amount: number): string {
  return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' }).format(amount);
}

function formatRelativeDate(dateStr: string): string {
  const date = new Date(dateStr);
  const now = new Date();
  const diffMs = now.getTime() - date.getTime();
  const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
  if (diffDays === 0) return 'Hoje';
  if (diffDays === 1) return 'Ontem';
  if (diffDays < 7) return `Há ${diffDays} dias`;
  return date.toLocaleDateString('pt-BR', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

// ─── Transaction row ──────────────────────────────────────────────────────────
function TransactionRow({
  item,
  onPress,
}: {
  item: TransactionResponse;
  onPress: (item: TransactionResponse) => void;
}) {
  const label = item.merchant ?? item.description ?? item.bank;
  return (
    <TouchableOpacity
      style={styles.txRow}
      onPress={() => onPress(item)}
      accessibilityLabel={`Transação: ${label}, ${formatBRL(item.amount)}`}
      activeOpacity={0.7}
    >
      <View style={styles.txIconWrap}>
        <Ionicons name={getCategoryIcon(item.category) as any} size={22} color={ACCENT} />
      </View>
      <View style={styles.txDetails}>
        <Text style={styles.txTitle} numberOfLines={1}>
          {label}
        </Text>
        <Text style={styles.txCategory}>{getCategoryLabel(item.category)}</Text>
      </View>
      <View style={styles.txRight}>
        <Text style={styles.txAmount}>{formatBRL(item.amount)}</Text>
        <Text style={styles.txDate}>{formatRelativeDate(item.eventTimestampUtc)}</Text>
      </View>
    </TouchableOpacity>
  );
}

// ─── Category picker modal ────────────────────────────────────────────────────
function CategoryPickerModal({
  visible,
  transaction,
  onClose,
  onSelect,
  isUpdating,
}: {
  visible: boolean;
  transaction: TransactionResponse | null;
  onClose: () => void;
  onSelect: (category: string) => void;
  isUpdating: boolean;
}) {
  if (!transaction) return null;
  const label = transaction.merchant ?? transaction.description ?? transaction.bank;
  return (
    <Modal
      visible={visible}
      transparent
      animationType="slide"
      onRequestClose={onClose}
    >
      <Pressable style={styles.modalOverlay} onPress={onClose}>
        <Pressable style={styles.modalSheet} onPress={() => undefined}>
          {/* Handle */}
          <View style={styles.modalHandle} />

          {/* Title */}
          <Text style={styles.modalTitle}>Editar categoria</Text>
          <Text style={styles.modalSubtitle} numberOfLines={1}>
            {label}
          </Text>
          <Text style={styles.modalAmount}>{formatBRL(transaction.amount)}</Text>

          {/* Categories list */}
          <ScrollView style={styles.categoriesScroll} showsVerticalScrollIndicator={false}>
            {PREDEFINED_CATEGORIES.map((cat) => {
              const selected = cat.value === transaction.category;
              return (
                <TouchableOpacity
                  key={cat.value}
                  style={[styles.categoryItem, selected && styles.categoryItemSelected]}
                  onPress={() => !isUpdating && onSelect(cat.value)}
                  accessibilityLabel={`Categoria: ${cat.label}`}
                  activeOpacity={0.7}
                >
                  <View style={styles.categoryItemLeft}>
                    <Ionicons name={cat.icon as any} size={20} color={selected ? PRIMARY : MUTED} style={styles.catItemIcon} />
                    <Text style={[styles.categoryItemLabel, selected && styles.categoryItemLabelSelected]}>
                      {cat.label}
                    </Text>
                  </View>
                  {selected && (
                    isUpdating
                      ? <ActivityIndicator size="small" color={PRIMARY} />
                      : <Ionicons name="checkmark-circle" size={20} color={PRIMARY} />
                  )}
                </TouchableOpacity>
              );
            })}
          </ScrollView>

          {/* Cancel */}
          <TouchableOpacity style={styles.cancelBtn} onPress={onClose} disabled={isUpdating}>
            <Text style={styles.cancelText}>Fechar</Text>
          </TouchableOpacity>
        </Pressable>
      </Pressable>
    </Modal>
  );
}

// ─── Main screen ──────────────────────────────────────────────────────────────
export default function TransactionsScreen() {
  const queryClient = useQueryClient();
  const { toast } = useToast();
  const [selectedTx, setSelectedTx] = useState<TransactionResponse | null>(null);
  const [modalVisible, setModalVisible] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const { data, isLoading, isError, refetch } = useQuery<GetTransactionsResponse>({
    queryKey: ['transactions', 1, 20],
    queryFn: async () => {
      const res = await transactionsApiClient.list({ page: 1, pageSize: 20 });
      return res.data;
    },
  });

  const { mutate: updateCategory, isPending: isUpdating } = useMutation({
    mutationFn: ({ id, category }: { id: string; category: string }) =>
      transactionsApiClient.updateCategory(id, category),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['transactions'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      setModalVisible(false);
      setSelectedTx(null);
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error('Não foi possível atualizar a categoria. Tente novamente.');
    },
  });

  const handleRefresh = useCallback(async () => {
    setRefreshing(true);
    await refetch();
    setRefreshing(false);
  }, [refetch]);

  const handleTxPress = useCallback((item: TransactionResponse) => {
    setSelectedTx(item);
    setModalVisible(true);
  }, []);

  const handleCategorySelect = useCallback(
    (category: string) => {
      if (!selectedTx) return;
      updateCategory({ id: selectedTx.id, category });
    },
    [selectedTx, updateCategory]
  );

  const handleCloseModal = useCallback(() => {
    if (isUpdating) return;
    setModalVisible(false);
    setSelectedTx(null);
  }, [isUpdating]);

  return (
    <SafeAreaView style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <Text style={styles.headerTitle}>Transações</Text>
        <View style={styles.headerRight}>
          {data && (
            <Text style={styles.headerCount}>{data.totalCount} no total</Text>
          )}
          <TouchableOpacity
            style={styles.importBtn}
            onPress={() => router.push('/(main)/transactions/new' as any)}
            accessibilityLabel="Adicionar transação manualmente"
            activeOpacity={0.8}
          >
            <Ionicons name="add" size={22} color={TEXT} />
          </TouchableOpacity>
          <TouchableOpacity
            style={styles.importBtn}
            onPress={() => router.push('/(main)/ocr-upload' as any)}
            accessibilityLabel="Importar extrato via OCR"
            activeOpacity={0.8}
          >
            <Ionicons name="cloud-upload-outline" size={20} color={TEXT} />
          </TouchableOpacity>
        </View>
      </View>

      {/* Loading state */}
      {isLoading && <LoadingState message="Carregando transações..." />}

      {/* Error state */}
      {isError && !isLoading && (
        <ErrorState
          message="Erro ao carregar transações"
          onRetry={handleRefresh}
        />
      )}

      {/* Data / empty state */}
      {!isLoading && !isError && (
        <FlatList
          data={data?.items ?? []}
          keyExtractor={(item) => item.id}
          renderItem={({ item }) => (
            <TransactionRow item={item} onPress={handleTxPress} />
          )}
          ListEmptyComponent={
            <EmptyState
              icon="receipt-outline"
              title="Nenhuma transação ainda"
              subtitle="As transações capturadas das notificações bancárias aparecerão aqui"
              ctaLabel="Importar extrato"
              onCtaPress={() => router.push('/(main)/ocr-upload' as any)}
            />
          }
          refreshControl={
            <RefreshControl refreshing={refreshing} onRefresh={handleRefresh} tintColor={ACCENT} />
          }
          contentContainerStyle={
            (data?.items?.length ?? 0) === 0 ? styles.flatListEmpty : styles.flatListContent
          }
          showsVerticalScrollIndicator={false}
          ItemSeparatorComponent={() => <View style={styles.separator} />}
        />
      )}

      {/* Category picker modal */}
      <CategoryPickerModal
        visible={modalVisible}
        transaction={selectedTx}
        onClose={handleCloseModal}
        onSelect={handleCategorySelect}
        isUpdating={isUpdating}
      />
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: BG },
  header: {
    paddingHorizontal: 20,
    paddingTop: 24,
    paddingBottom: 16,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  headerTitle: { fontSize: 22, fontWeight: '700', color: TEXT },
  headerRight: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  headerCount: { fontSize: 13, color: MUTED },
  importBtn: {
    width: 36,
    height: 36,
    borderRadius: 18,
    backgroundColor: PRIMARY,
    alignItems: 'center',
    justifyContent: 'center',
  },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  loadingText: { color: MUTED, marginTop: 12, fontSize: 14 },
  flatListContent: { paddingHorizontal: 16, paddingBottom: 24 },
  flatListEmpty: { flex: 1, justifyContent: 'center' },
  separator: { height: 1, backgroundColor: BORDER, marginHorizontal: 16 },
  txRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: CARD,
    paddingHorizontal: 16,
    paddingVertical: 14,
    minHeight: 64,
  },
  txIconWrap: {
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: BG,
    alignItems: 'center',
    justifyContent: 'center',
    marginRight: 12,
  },
  txDetails: { flex: 1, marginRight: 8 },
  txTitle: { fontSize: 14, fontWeight: '600', color: TEXT },
  txCategory: { fontSize: 12, color: MUTED, marginTop: 2 },
  txRight: { alignItems: 'flex-end' },
  txAmount: { fontSize: 14, fontWeight: '700', color: TEXT },
  txDate: { fontSize: 11, color: MUTED, marginTop: 2 },
  errorWrap: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 32 },
  errorTitle: { color: TEXT, fontSize: 16, fontWeight: '600', marginTop: 12 },
  errorHint: { color: MUTED, fontSize: 13, marginTop: 6, textAlign: 'center' },
  retryBtn: {
    marginTop: 20,
    backgroundColor: PRIMARY,
    borderRadius: 10,
    paddingHorizontal: 24,
    paddingVertical: 12,
  },
  retryText: { color: TEXT, fontSize: 14, fontWeight: '600' },
  emptyWrap: { alignItems: 'center', padding: 48 },
  emptyTitle: { color: TEXT, fontSize: 16, fontWeight: '600', marginTop: 16 },
  emptyHint: { color: MUTED, fontSize: 13, marginTop: 8, textAlign: 'center' },
  // Modal
  modalOverlay: {
    flex: 1,
    backgroundColor: OVERLAY,
    justifyContent: 'flex-end',
  },
  modalSheet: {
    backgroundColor: CARD,
    borderTopLeftRadius: 24,
    borderTopRightRadius: 24,
    paddingBottom: 32,
    maxHeight: '80%',
  },
  modalHandle: {
    width: 40,
    height: 4,
    backgroundColor: BORDER,
    borderRadius: 2,
    alignSelf: 'center',
    marginTop: 12,
    marginBottom: 16,
  },
  modalTitle: {
    fontSize: 17,
    fontWeight: '700',
    color: TEXT,
    textAlign: 'center',
    marginBottom: 4,
  },
  modalSubtitle: {
    fontSize: 13,
    color: MUTED,
    textAlign: 'center',
    paddingHorizontal: 24,
  },
  modalAmount: {
    fontSize: 20,
    fontWeight: '700',
    color: TEXT,
    textAlign: 'center',
    marginTop: 4,
    marginBottom: 16,
  },
  categoriesScroll: { maxHeight: 320, paddingHorizontal: 16 },
  categoryItem: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 14,
    paddingHorizontal: 16,
    borderRadius: 12,
    marginBottom: 4,
    borderWidth: 1,
    borderColor: 'transparent',
  },
  categoryItemSelected: {
    backgroundColor: 'rgba(99,102,241,0.12)',
    borderColor: PRIMARY,
  },
  categoryItemLeft: { flexDirection: 'row', alignItems: 'center' },
  catItemIcon: { marginRight: 12 },
  categoryItemLabel: { fontSize: 15, color: TEXT },
  categoryItemLabelSelected: { color: ACCENT, fontWeight: '600' },
  cancelBtn: {
    marginHorizontal: 16,
    marginTop: 8,
    backgroundColor: BG,
    borderRadius: 12,
    paddingVertical: 14,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: BORDER,
  },
  cancelText: { color: MUTED, fontSize: 15, fontWeight: '600' },
});
