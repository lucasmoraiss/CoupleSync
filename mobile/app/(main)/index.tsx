// AC-004: Dashboard screen — real API data via TanStack Query
import { getApiErrorMessage } from '@/services/apiError';
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
import { dashboardApiClient, coupleApiClient } from '@/services/apiClient';
import { useSessionStore } from '@/state/sessionStore';
import { useDashboardStore } from '@/state/dashboardStore';
import { memberLabel, monthLabelFromIso, type MemberName } from '@/utils/format';
import { getCategoryLabel, getCategoryIcon } from '@/modules/transactions/categories';
import type { DashboardResponse } from '@/types/api';
import { colors } from '@/theme';
import { LoadingState } from '@/components/LoadingState';
import { EmptyState } from '@/components/EmptyState';
import { ErrorState } from '@/components/ErrorState';
import { EmailVerificationBanner } from '@/components/EmailVerificationBanner';
import { GroupSwitcher } from '@/components/GroupSwitcher';
import { spokenBRL } from '@/utils/a11y';
import { AI_STATUS_NOTICE_TEXT, aiStatusNotice, isAssistantVisible, shouldShowActivationCard } from '@/modules/ai/aiStatus';
import { openWelcomeIfDue } from '@/modules/ai/aiStatusStore';
import { useAiStatus } from '@/modules/ai/useAiStatus';
import { isCaptureConsentAhead } from '@/modules/integrations/notification-capture/useCaptureConsentSync';
import { useOnRefocus } from '@/navigation/resetOnFocus';
import {
  RECURRING_CARD_EMPTY_LABEL,
  RECURRING_CARD_EMPTY_TEXT,
  RECURRING_CARD_ERROR_LABEL,
  RECURRING_CARD_ERROR_TEXT,
  isRecurringEmpty,
  recurringCardLabel,
  recurringCardText,
} from '@/modules/recurring/recurring';
import { useRecurring } from '@/modules/recurring/useRecurring';

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

// ─── Sub-components ───────────────────────────────────────────────────────────
function TotalExpensesCard({ data }: { data: DashboardResponse }) {
  return (
    <View style={styles.card}>
      <Text style={styles.cardLabel}>Total de gastos</Text>
      <Text style={styles.cardValue} accessibilityLabel={spokenBRL(data.totalExpenses)}>{formatBRL(data.totalExpenses)}</Text>
      <Text style={styles.cardHint}>{data.transactionCount} transações</Text>
    </View>
  );
}

function PartnerBreakdownRow({
  data,
  currentUserId,
  members,
}: {
  data: DashboardResponse;
  currentUserId: string | null;
  members: readonly MemberName[] | undefined;
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
            {memberLabel(p.userId, currentUserId, members)}
          </Text>
          <Text style={styles.miniValue} accessibilityLabel={spokenBRL(p.totalAmount)}>{formatBRL(p.totalAmount)}</Text>
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
      <Text style={styles.sectionTitle} accessibilityRole="header">Por categoria</Text>
      {entries.map(([cat, amount]) => (
        <View key={cat} style={styles.categoryRow}>
          <View style={styles.categoryLeft}>
            <Ionicons name={getCategoryIcon(cat) as any} size={20} color={colors.primaryLight} style={styles.categoryIcon} />
            <Text style={styles.categoryName}>{getCategoryLabel(cat)}</Text>
          </View>
          <Text style={styles.categoryAmount} accessibilityLabel={spokenBRL(amount)}>{formatBRL(amount)}</Text>
        </View>
      ))}
    </View>
  );
}

// ─── Main screen ──────────────────────────────────────────────────────────────
export default function DashboardScreen() {
  const userId = useSessionStore((s) => s.userId);
  const { startDate, endDate } = useDashboardStore();

  const { data, isLoading, isError, error: loadError, refetch } = useQuery<DashboardResponse>({
    queryKey: ['dashboard', startDate, endDate],
    queryFn: async () => {
      const res = await dashboardApiClient.get({ startDate, endDate });
      return res.data;
    },
  });

  // Nomes dos membros do grupo, para rotular a divisão dos gastos.
  const { data: couple } = useQuery({
    queryKey: ['couple', 'me'],
    queryFn: async () => (await coupleApiClient.getMyCouple()).data,
    staleTime: 5 * 60 * 1000,
  });

  // Análise com IA: o status é consultado a cada vez que o Painel recebe foco. Quem ainda não respondeu à pergunta
  // (ou não viu que o outro membro ativou) é levado à tela de boas-vindas, uma vez por abertura do app.
  // Vale cada resposta do servidor (a do foco, a de uma nova tentativa depois de uma falha). Se o consentimento da
  // captura vai abrir sozinho, ele vem primeiro e a pergunta da IA fica para a próxima volta ao Painel.
  const ai = useAiStatus((fresh) => {
    void openWelcomeIfDue(fresh, {
      isFocused: ai.isFocused,
      otherPromptPending: isCaptureConsentAhead,
      open: () => router.push('/(main)/ai/welcome' as any),
    });
  });
  const aiStatus = ai.status;
  // Sem status nenhum (nem o guardado no aparelho): o Painel diz o que está acontecendo com a IA, em vez de
  // simplesmente não mostrar nada dela.
  const aiNotice = aiStatusNotice(aiStatus, ai.loadFailed, ai.retrying);

  // Assinaturas e recorrências (sem IA, vale para qualquer grupo): o cartão está sempre no Painel — com o total,
  // com o vazio que explica o que vai aparecer, carregando, ou em erro com "tentar de novo". O Painel fica montado
  // entre visitas, então pergunta de novo a cada volta.
  const recurring = useRecurring();
  useOnRefocus(() => void recurring.refetch({ cancelRefetch: false }));

  const [refreshing, setRefreshing] = React.useState(false);
  const handleRefresh = async () => {
    setRefreshing(true);
    // As recorrências são pedidas junto e esperadas no fim (em erro o cartão mostra o próprio estado).
    const recurringRefresh = recurring.refetch();
    // Puxar para atualizar também consulta o status da IA (não só os números do Painel).
    await Promise.all([refetch(), ai.refresh()]);
    await recurringRefresh;
    setRefreshing(false);
  };

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
            <Text style={styles.greeting}>Painel</Text>
            {data ? (
              <Text style={styles.subtitle}>{monthLabelFromIso(data.periodStart)}</Text>
            ) : (
              <Text style={styles.subtitle}>Resumo financeiro do grupo</Text>
            )}
          </View>
          <View style={styles.headerActions}>
            {/* Só existe quando o servidor diz que há IA (GET /ai/status: available). */}
            {isAssistantVisible(aiStatus) && (
              <TouchableOpacity
                accessibilityRole="button"
                style={styles.assistantBtn}
                onPress={() => router.push('/(main)/chat' as any)}
                accessibilityLabel="Abrir o Assistente"
              >
                <Ionicons name="sparkles" size={18} color={colors.primaryLight} />
                <Text style={styles.assistantBtnText}>Assistente</Text>
              </TouchableOpacity>
            )}
            <TouchableOpacity accessibilityRole="button"
              style={styles.transactionsBtn}
              onPress={() => router.push('/transactions' as any)}
              accessibilityLabel="Ver transações"
            >
              <Ionicons name="receipt-outline" size={22} color={colors.primaryLight} />
            </TouchableOpacity>
          </View>
        </View>

        <GroupSwitcher />

        <EmailVerificationBanner />

        {/* Enquanto o grupo não ativou a análise com IA (os números do Painel continuam iguais). */}
        {shouldShowActivationCard(aiStatus) && (
          <TouchableOpacity
            accessibilityRole="button"
            style={styles.aiCard}
            onPress={() => router.push('/(main)/ai/welcome' as any)}
            accessibilityLabel="Análise com IA desligada. Ativar"
          >
            <Ionicons name="sparkles-outline" size={20} color={colors.primaryLight} />
            <Text style={styles.aiCardText}>Análise com IA desligada</Text>
            <Text style={styles.aiCardAction}>Ativar</Text>
          </TouchableOpacity>
        )}

        {/* O status da IA ainda não chegou e não há valor guardado: verificando, tentando de novo ou falhou. */}
        {aiNotice !== 'none' && (
          <View style={styles.aiCard}>
            <Ionicons name={aiNotice === 'checking' ? 'sparkles-outline' : 'cloud-offline-outline'} size={20} color={colors.textMuted} />
            <Text style={styles.aiNoticeText} accessibilityLiveRegion="polite">{AI_STATUS_NOTICE_TEXT[aiNotice]}</Text>
            {aiNotice !== 'checking' && (
              <TouchableOpacity
                accessibilityRole="button"
                style={styles.aiNoticeBtn}
                onPress={() => void ai.refresh()}
                accessibilityLabel="Verificar a análise com IA agora"
              >
                <Text style={styles.aiCardAction}>Verificar agora</Text>
              </TouchableOpacity>
            )}
          </View>
        )}

        {/* Assinaturas e recorrências: quanto o grupo paga por mês no que se repete. Nunca some em silêncio. */}
        {recurring.data ? (
          <TouchableOpacity
            accessibilityRole="button"
            style={styles.aiCard}
            onPress={() => router.push('/(main)/recurring' as any)}
            accessibilityLabel={isRecurringEmpty(recurring.data) ? RECURRING_CARD_EMPTY_LABEL : recurringCardLabel(recurring.data)}
          >
            <Ionicons name="repeat-outline" size={20} color={colors.primaryLight} />
            <Text style={styles.aiCardText}>
              {isRecurringEmpty(recurring.data) ? RECURRING_CARD_EMPTY_TEXT : recurringCardText(recurring.data)}
            </Text>
            <Text style={styles.aiCardAction}>Ver</Text>
          </TouchableOpacity>
        ) : recurring.isError ? (
          <TouchableOpacity
            accessibilityRole="button"
            style={styles.aiCard}
            onPress={() => void recurring.refetch()}
            accessibilityLabel={RECURRING_CARD_ERROR_LABEL}
          >
            <Ionicons name="alert-circle-outline" size={20} color={colors.warning} />
            <Text style={styles.aiCardText}>{RECURRING_CARD_ERROR_TEXT}</Text>
            <Text style={styles.aiCardAction}>Tentar de novo</Text>
          </TouchableOpacity>
        ) : (
          <View style={styles.aiCard} accessible accessibilityLabel="Carregando as recorrências">
            <Ionicons name="repeat-outline" size={20} color={colors.textMuted} />
            <Text style={styles.aiCardText}>Carregando as recorrências…</Text>
          </View>
        )}

        {/* Loading state */}
        {isLoading && <LoadingState />}

        {/* Error state */}
        {isError && !isLoading && (
          <ErrorState
            message={getApiErrorMessage(loadError, 'Não foi possível carregar os dados.')}
            onRetry={refetch}
          />
        )}

        {/* Data state */}
        {data && !isLoading && (
          <>
            <TotalExpensesCard data={data} />
            <PartnerBreakdownRow data={data} currentUserId={userId} members={couple?.members} />
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
          <TouchableOpacity accessibilityRole="button"
            style={styles.incomeChip}
            onPress={() => router.push('/(main)/budget' as any)}
            accessibilityLabel="Ver rendas"
          >
            <Ionicons name="wallet-outline" size={18} color={colors.primaryLight} />
            <Text style={styles.incomeChipText}>Ver rendas</Text>
          </TouchableOpacity>
        )}

        {/* Transactions shortcut */}
        {!isLoading && (
          <TouchableOpacity accessibilityRole="button"
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

    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: BG, paddingHorizontal: 20, paddingTop: 24 },
  header: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginBottom: 24 },
  greeting: { fontSize: 22, fontWeight: '700', color: TEXT },
  subtitle: { fontSize: 14, color: MUTED, marginTop: 2 },
  transactionsBtn: { minHeight: 44, minWidth: 44, backgroundColor: CARD, borderRadius: 10, padding: 10, borderWidth: 1, borderColor: BORDER },
  headerActions: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  assistantBtn: {
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    backgroundColor: CARD,
    borderRadius: 10,
    paddingHorizontal: 12,
    borderWidth: 1,
    borderColor: PRIMARY,
  },
  assistantBtnText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  aiCard: {
    minHeight: 48,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    backgroundColor: CARD,
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 12,
    marginBottom: 16,
    borderWidth: 1,
    borderColor: BORDER,
  },
  aiCardText: { flex: 1, color: TEXT, fontSize: 14, fontWeight: '500' },
  aiCardAction: { color: colors.primaryLight, fontSize: 14, fontWeight: '700' },
  aiNoticeText: { flex: 1, color: MUTED, fontSize: 13, lineHeight: 18 },
  aiNoticeBtn: { minHeight: 44, justifyContent: 'center', paddingLeft: 8 },
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
