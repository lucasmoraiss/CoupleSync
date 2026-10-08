// AC-003: Transactions list screen — FlatList + pull-to-refresh + inline category editor
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
  Alert,
} from 'react-native';
import { router } from 'expo-router';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import { transactionsApiClient, isCoupleRequiredError } from '@/services/apiClient';
import {
  getCategoryLabel,
  getCategoryIcon,
  toCategoryKey,
} from '@/modules/transactions/categories';
import { useCategories } from '@/modules/transactions/useCategories';
import { getTransactionSubtitle, getTransactionTitle } from '@/modules/transactions/display';
import {
  TRANSACTIONS_PAGE_SIZE,
  flattenTransactionPages,
  getNextTransactionsPage,
} from '@/modules/transactions/pagination';
import type { TransactionResponse, GetTransactionsResponse } from '@/types/api';
import { colors } from '@/theme';
import { openNotificationListenerSettings } from '@/modules/integrations/notification-capture/NotificationListenerBridge';
import { useNotificationPermission } from '@/modules/integrations/notification-capture/useNotificationPermission';
import { captureBannerText } from '@/modules/privacy/captureStatus';
import { LoadingState } from '@/components/LoadingState';
import { EmptyState } from '@/components/EmptyState';
import { ErrorState } from '@/components/ErrorState';
import { useToast } from '@/components/Toast/useToast';
import { useSessionStore } from '@/state/sessionStore';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { usePendingBankReviewCount } from '@/modules/openfinance/useBankReview';
import { reviewShortcutLabel } from '@/modules/openfinance/review';
import { useOnRefocus } from '@/navigation/resetOnFocus';
import { spokenBRL } from '@/utils/a11y';

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
const WARNING = colors.warning;

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

function getDisplayAuthorName(item: TransactionResponse, currentUserId: string | null): string {
  if (item.userId === currentUserId) {
    return 'Você';
  }

  const trimmedName = item.authorName.trim();
  if (!trimmedName) {
    return 'Desconhecido';
  }

  return trimmedName.split(' ')[0] ?? trimmedName;
}

// ─── Source badge helper ──────────────────────────────────────────────────────
const SOURCE_BADGE_CONFIG: Record<string, { label: string; bg: string; text: string }> = {
  Manual: { label: 'Manual', bg: '#E0E0E0', text: '#616161' },
  OcrImport: { label: 'OCR', bg: '#BBDEFB', text: '#1565C0' },
  Notification: { label: 'Notificação', bg: '#C8E6C9', text: '#2E7D32' },
  OpenFinance: { label: 'Banco', bg: '#D1C4E9', text: '#4527A0' },
};

function getSourceBadge(source: string | undefined) {
  const config = SOURCE_BADGE_CONFIG[source ?? 'Manual'] ?? SOURCE_BADGE_CONFIG.Manual;
  return config;
}

// ─── Transaction row ──────────────────────────────────────────────────────────
function TransactionRow({
  item,
  currentUserId,
  onPress,
  onLongPress,
  onEdit,
  onDelete,
}: {
  item: TransactionResponse;
  currentUserId: string | null;
  onPress: (item: TransactionResponse) => void;
  onLongPress: (item: TransactionResponse) => void;
  onEdit: (item: TransactionResponse) => void;
  onDelete: (item: TransactionResponse) => void;
}) {
  const label = getTransactionTitle(item);
  const subtitle = getTransactionSubtitle(item);
  const authorLabel = getDisplayAuthorName(item, currentUserId);
  const sourceBadge = getSourceBadge(item.source);
  return (
    <TouchableOpacity accessibilityRole="button"
      style={styles.txRow}
      onPress={() => onPress(item)}
      onLongPress={() => onLongPress(item)}
      accessibilityLabel={`Transação: ${label}, ${spokenBRL(item.amount)}, ${getCategoryLabel(item.category)}, ${formatRelativeDate(item.eventTimestampUtc)}, ${authorLabel}`}
      accessibilityHint="Toque para alterar a categoria"
      // Os botões de editar e excluir ficam dentro da linha, e o leitor de tela trata a linha como um só item:
      // por isso as mesmas duas ações são oferecidas como ações personalizadas (deslizar para cima/baixo).
      accessibilityActions={[
        { name: 'edit', label: 'Editar transação' },
        { name: 'delete', label: 'Excluir transação' },
      ]}
      onAccessibilityAction={(event) => {
        if (event.nativeEvent.actionName === 'edit') onEdit(item);
        else if (event.nativeEvent.actionName === 'delete') onDelete(item);
      }}
      activeOpacity={0.7}
    >
      <View style={styles.txIconWrap}>
        <Ionicons name={getCategoryIcon(item.category) as any} size={22} color={ACCENT} />
      </View>
      <View style={styles.txDetails}>
        <Text style={styles.txTitle} numberOfLines={1}>
          {label}
        </Text>
        {subtitle && (
          <Text style={styles.txSubtitle} numberOfLines={1}>
            {subtitle}
          </Text>
        )}
        <View style={styles.txMetaRow}>
          <Text style={styles.txCategory}>{getCategoryLabel(item.category)}</Text>
          <View style={[styles.sourceBadge, { backgroundColor: sourceBadge.bg }]}>
            <Text style={[styles.sourceBadgeText, { color: sourceBadge.text }]}>{sourceBadge.label}</Text>
          </View>
        </View>
        <Text style={styles.txAuthor}>{authorLabel}</Text>
      </View>
      <View style={styles.txRight}>
        <Text style={styles.txAmount} accessibilityLabel={spokenBRL(item.amount)}>{formatBRL(item.amount)}</Text>
        <Text style={styles.txDate}>{formatRelativeDate(item.eventTimestampUtc)}</Text>
      </View>
      <TouchableOpacity accessibilityRole="button"
        onPress={() => onEdit(item)}
        style={styles.deleteBtn}
        accessibilityLabel={`Editar transação ${label}`}
        hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
      >
        <Ionicons name="create-outline" size={18} color={ACCENT} />
      </TouchableOpacity>
      <TouchableOpacity accessibilityRole="button"
        onPress={() => onDelete(item)}
        style={styles.deleteBtn}
        accessibilityLabel={`Excluir transação ${label}`}
        hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
      >
        <Ionicons name="trash-outline" size={18} color={ERROR} />
      </TouchableOpacity>
    </TouchableOpacity>
  );
}

// ─── Category picker modal ────────────────────────────────────────────────────
function CategoryPickerModal({
  visible,
  transaction,
  onClose,
  onSelect,
  onEdit,
  isUpdating,
}: {
  visible: boolean;
  transaction: TransactionResponse | null;
  onClose: () => void;
  onSelect: (category: string) => void;
  onEdit: (transaction: TransactionResponse) => void;
  isUpdating: boolean;
}) {
  const categories = useCategories();
  if (!transaction) return null;
  const label = getTransactionTitle(transaction);
  const currentKey = toCategoryKey(transaction.category);
  return (
    <Modal
      visible={visible}
      transparent
      animationType="slide"
      onRequestClose={onClose}
    >
      <Pressable accessibilityRole="button" accessibilityLabel="Fechar" style={styles.modalOverlay} onPress={onClose}>
        <Pressable accessible={false} style={styles.modalSheet} onPress={() => undefined}>
          {/* Handle */}
          <View style={styles.modalHandle} />

          {/* Title */}
          <Text style={styles.modalTitle} accessibilityRole="header">Editar categoria</Text>
          <Text style={styles.modalSubtitle} numberOfLines={1}>
            {label}
          </Text>
          <Text style={styles.modalAmount} accessibilityLabel={spokenBRL(transaction.amount)}>{formatBRL(transaction.amount)}</Text>

          {/* Categories list */}
          <ScrollView style={styles.categoriesScroll} showsVerticalScrollIndicator={false}>
            {categories.map((cat) => {
              const selected = cat.value === currentKey;
              return (
                <TouchableOpacity accessibilityRole="radio" accessibilityState={{ selected }}
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

          <TouchableOpacity accessibilityRole="button"
            style={styles.cancelBtn}
            onPress={() => onEdit(transaction)}
            disabled={isUpdating}
            accessibilityLabel="Editar transação"
          >
            <Text style={[styles.cancelText, { color: PRIMARY }]}>Editar transação</Text>
          </TouchableOpacity>

          {/* Cancel */}
          <TouchableOpacity accessibilityRole="button" accessibilityLabel="Fechar" style={styles.cancelBtn} onPress={onClose} disabled={isUpdating}>
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
  const currentUserId = useSessionStore((state) => state.userId);
  const [selectedTx, setSelectedTx] = useState<TransactionResponse | null>(null);
  const [modalVisible, setModalVisible] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  // Conferida a cada volta à tela e ao app (a aba fica montada; a permissão muda fora do app).
  const notifPermission = useNotificationPermission();
  const captureConsent = useConsentStore((state) => state.record.capture);
  const captureBanner = captureBannerText(captureConsent, notifPermission);
  // Open Finance: quantas despesas do banco esperam a revisão. O atalho só aparece quando há alguma; a contagem
  // é refeita a cada volta à tela (a aba fica montada).
  const bankReview = usePendingBankReviewCount();
  const bankReviewShortcut = reviewShortcutLabel(bankReview.pending);
  useOnRefocus(() => bankReview.refetch());

  const {
    data,
    isLoading,
    isError,
    error: loadError,
    refetch,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery<GetTransactionsResponse, Error, { pages: GetTransactionsResponse[] }, string[], number>({
    queryKey: ['transactions', 'list'],
    initialPageParam: 1,
    queryFn: async ({ pageParam }) => {
      const res = await transactionsApiClient.list({ page: pageParam, pageSize: TRANSACTIONS_PAGE_SIZE });
      return res.data;
    },
    getNextPageParam: (lastPage, allPages) => getNextTransactionsPage(lastPage, allPages),
  });

  const transactions = flattenTransactionPages(data?.pages);
  const totalCount = data?.pages[0]?.totalCount;

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
      toast.error(getApiErrorMessage(error, 'Não foi possível atualizar a categoria. Tente novamente.'));
    },
  });

  const { mutate: deleteTransaction } = useMutation({
    mutationFn: (id: string) => transactionsApiClient.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['transactions'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
      queryClient.invalidateQueries({ queryKey: ['reports'] });
      queryClient.invalidateQueries({ queryKey: ['budget'] });
      toast.success('Transação excluída');
    },
    onError: (error) => {
      if (isCoupleRequiredError(error)) return;
      toast.error(getApiErrorMessage(error, 'Não foi possível excluir a transação. Tente novamente.'));
    },
  });

  // Puxar para atualizar volta à primeira página (em vez de rebuscar todas as já carregadas).
  const handleRefresh = useCallback(async () => {
    setRefreshing(true);
    queryClient.setQueryData<{ pages: GetTransactionsResponse[]; pageParams: number[] }>(
      ['transactions', 'list'],
      (old) => (old ? { pages: old.pages.slice(0, 1), pageParams: old.pageParams.slice(0, 1) } : old),
    );
    await refetch();
    setRefreshing(false);
  }, [queryClient, refetch]);

  const handleEndReached = useCallback(() => {
    if (hasNextPage && !isFetchingNextPage) fetchNextPage();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const handleEdit = useCallback((item: TransactionResponse) => {
    setModalVisible(false);
    setSelectedTx(null);
    router.push({
      pathname: '/(main)/transactions/edit',
      params: {
        id: item.id,
        amount: String(item.amount),
        description: item.description ?? '',
        merchant: item.merchant ?? '',
        category: item.category,
        eventTimestampUtc: item.eventTimestampUtc,
      },
    } as any);
  }, []);

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

  const handleLongPress = useCallback(
    (item: TransactionResponse) => {
      const label = getTransactionTitle(item);
      Alert.alert(
        'Excluir transação',
        `Tem certeza que deseja excluir esta transação?\n\n${label} — ${formatBRL(item.amount)}`,
        [
          { text: 'Cancelar', style: 'cancel' },
          {
            text: 'Excluir',
            style: 'destructive',
            onPress: () => deleteTransaction(item.id),
          },
        ]
      );
    },
    [deleteTransaction]
  );

  const handleDeletePress = useCallback(
    (item: TransactionResponse) => {
      handleLongPress(item);
    },
    [handleLongPress]
  );

  return (
    <SafeAreaView style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <Text style={styles.headerTitle} accessibilityRole="header">Transações</Text>
        <View style={styles.headerRight}>
          {totalCount != null && (
            <Text style={styles.headerCount}>{totalCount} no total</Text>
          )}
          <TouchableOpacity accessibilityRole="button"
            style={styles.addBtn}
            onPress={() => router.push('/(main)/transactions/new' as any)}
            accessibilityLabel="Adicionar transação manualmente"
            activeOpacity={0.8}
          >
            <Ionicons name="add" size={20} color="#fff" />
            <Text style={styles.addBtnLabel}>Nova</Text>
          </TouchableOpacity>
          <TouchableOpacity accessibilityRole="button"
            style={styles.importBtn}
            onPress={() => router.push('/(main)/ocr-upload' as any)}
            accessibilityLabel="Importar extrato via OCR"
            activeOpacity={0.8}
          >
            <Ionicons name="cloud-upload-outline" size={20} color={TEXT} />
          </TouchableOpacity>
        </View>
      </View>

      {/* Open Finance: atalho para a revisão do banco, só quando há algo esperando */}
      {bankReviewShortcut !== null && (
        <TouchableOpacity
          style={styles.bankReviewShortcut}
          onPress={() => router.push('/(main)/openfinance/review' as any)}
          accessibilityRole="button"
          accessibilityLabel={`${bankReviewShortcut}. Abrir a revisão do banco`}
          activeOpacity={0.8}
        >
          <Ionicons name="business-outline" size={18} color={ACCENT} />
          <Text style={styles.bannerText}>{bankReviewShortcut}</Text>
          <Ionicons name="chevron-forward" size={18} color={ACCENT} />
        </TouchableOpacity>
      )}

      {/* Notification permission banner */}
      {captureBanner !== null && (
        <View style={styles.permissionBanner}>
          <Ionicons name="notifications-off-outline" size={18} color={WARNING} />
          <Text style={styles.bannerText}>{captureBanner}</Text>
          <TouchableOpacity
            onPress={() => {
              // Sem aceite registrado, ativar passa primeiro pela tela de consentimento.
              if (useConsentStore.getState().record.capture.acceptedAt === null) {
                router.push('/(main)/settings/capture-consent' as any);
              } else {
                openNotificationListenerSettings();
              }
            }}
            accessibilityRole="button"
            accessibilityLabel="Ativar a captura de notificações"
            style={{ minHeight: 44, minWidth: 44, justifyContent: 'center', alignItems: 'center' }}
          >
            <Text style={styles.bannerAction}>Ativar</Text>
          </TouchableOpacity>
        </View>
      )}

      {/* Loading state */}
      {isLoading && <LoadingState message="Carregando transações..." />}

      {/* Error state */}
      {isError && !isLoading && (
        <ErrorState
          message={getApiErrorMessage(loadError, 'Erro ao carregar transações.')}
          onRetry={handleRefresh}
        />
      )}

      {/* Data / empty state */}
      {!isLoading && !isError && (
        <FlatList
          data={transactions}
          keyExtractor={(item) => item.id}
          renderItem={({ item }) => (
            <TransactionRow
              item={item}
              currentUserId={currentUserId}
              onPress={handleTxPress}
              onLongPress={handleLongPress}
              onEdit={handleEdit}
              onDelete={handleDeletePress}
            />
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
          onEndReached={handleEndReached}
          onEndReachedThreshold={0.4}
          ListFooterComponent={
            isFetchingNextPage ? (
              <View style={styles.listFooter} accessibilityLabel="Carregando mais transações">
                <ActivityIndicator size="small" color={ACCENT} />
              </View>
            ) : !hasNextPage && transactions.length > 0 ? (
              <View style={styles.listFooter}>
                <Text style={styles.listFooterText}>Fim da lista</Text>
              </View>
            ) : null
          }
          contentContainerStyle={
            transactions.length === 0 ? styles.flatListEmpty : styles.flatListContent
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
        onEdit={handleEdit}
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
    minHeight: 44,
    minWidth: 44,
    width: 36,
    height: 36,
    borderRadius: 18,
    backgroundColor: PRIMARY,
    alignItems: 'center',
    justifyContent: 'center',
  },
  addBtn: {
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 20,
    backgroundColor: PRIMARY,
    gap: 4,
  },
  addBtnLabel: { fontSize: 13, fontWeight: '600', color: '#fff' },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  loadingText: { color: MUTED, marginTop: 12, fontSize: 14 },
  flatListContent: { paddingHorizontal: 16, paddingBottom: 24 },
  flatListEmpty: { flex: 1, justifyContent: 'center' },
  listFooter: { paddingVertical: 20, alignItems: 'center' },
  listFooterText: { color: MUTED, fontSize: 13 },
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
  txSubtitle: { fontSize: 12, color: MUTED, marginTop: 1 },
  txTitle: { fontSize: 14, fontWeight: '600', color: TEXT },
  txMetaRow: { flexDirection: 'row', alignItems: 'center', marginTop: 2, gap: 6 },
  txCategory: { fontSize: 12, color: MUTED },
  sourceBadge: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 4,
  },
  sourceBadgeText: { fontSize: 10, fontWeight: '600' },
  txAuthor: { fontSize: 12, color: ACCENT, marginTop: 2, fontWeight: '500' },
  txRight: { alignItems: 'flex-end' },
  deleteBtn: {
    width: 32,
    height: 32,
    alignItems: 'center',
    justifyContent: 'center',
    marginLeft: 8,
  },
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
  permissionBanner: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: 'rgba(245,158,11,0.12)',
    borderLeftWidth: 3,
    borderLeftColor: WARNING,
    marginHorizontal: 16,
    marginTop: 8,
    marginBottom: 4,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 8,
    gap: 8,
  },
  bankReviewShortcut: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: 'rgba(99,102,241,0.14)',
    borderLeftWidth: 3,
    borderLeftColor: ACCENT,
    marginHorizontal: 16,
    marginTop: 8,
    marginBottom: 4,
    paddingHorizontal: 12,
    minHeight: 48,
    borderRadius: 8,
    gap: 8,
  },
  bannerText: { flex: 1, fontSize: 13, color: TEXT },
  bannerAction: { fontSize: 13, fontWeight: '700', color: WARNING },
});
