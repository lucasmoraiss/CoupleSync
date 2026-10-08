// Assinaturas e recorrências (aba oculta, pai Painel): o que o grupo paga todo mês, toda semana ou todo ano,
// calculado no servidor sem IA. Total mensal e anual; Assinaturas, Contas fixas, Parcelamentos, Pequenos gastos
// frequentes e Ocultas; tocar num item mostra as cobranças dele e as correções ("Não é recorrente", "Cancelei",
// "É assinatura", "É conta fixa"), que o servidor guarda e respeita nos próximos cálculos.
//
// A tela é remontada a cada visita (resetOnFocus): o item aberto e os avisos de uma visita não ficam para a
// seguinte, e a lista é a que o servidor tem agora. Não há campo de texto.
import React, { useState } from 'react';
import { ActivityIndicator, Alert, RefreshControl, SafeAreaView, ScrollView, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { useQueryClient } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import { recurringApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { getSessionEpoch } from '@/state/sessionStore';
import { ErrorState } from '@/components/ErrorState';
import { LoadingState } from '@/components/LoadingState';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';
import { spokenBRL } from '@/utils/a11y';
import { formatBrazilDate } from '@/utils/brazilDateTime';
import {
  RECURRING_EMPTY_TEXT,
  RECURRING_HINT,
  RECURRING_NOT_LISTED_NOTE,
  RECURRING_QUERY_KEY,
  RECURRING_STALE_TEXT,
  RECURRING_TITLE,
  RECURRING_TOTALS_NOTE,
  amountText,
  formatBRL,
  isRecurringEmpty,
  itemActions,
  itemBadges,
  itemDetail,
  itemLabel,
  overrideDoneMessage,
  recurringSections,
  type RecurringAction,
} from '@/modules/recurring/recurring';
import { useRecurring, useRecurringCharges } from '@/modules/recurring/useRecurring';
import type { RecurringItemResponse } from '@/types/api';

function leave() {
  goToParent('recurring/index');
}

/** As cobranças de um item aberto. */
function Charges({ id }: { id: string }) {
  const { data, isLoading, isError, refetch } = useRecurringCharges(id);

  if (isLoading) return <ActivityIndicator color={colors.primary} accessibilityLabel="Carregando as cobranças" style={styles.chargesLoading} />;
  if (isError || !data) {
    return (
      <TouchableOpacity
        onPress={() => void refetch()}
        accessibilityRole="button"
        accessibilityLabel="Não foi possível carregar as cobranças. Tentar de novo"
        style={styles.retryRow}
      >
        <Text style={styles.muted}>Não foi possível carregar as cobranças. Toque para tentar de novo.</Text>
      </TouchableOpacity>
    );
  }

  if (data.transactions.length === 0) return <Text style={styles.muted}>Nenhuma cobrança guardada para este item.</Text>;

  return (
    <View>
      <Text style={styles.chargesTitle}>Cobranças</Text>
      {data.transactions.map((charge) => (
        <View key={charge.transactionId} style={styles.chargeRow}>
          <Text style={styles.chargeDate}>{formatBrazilDate(charge.date)}</Text>
          <Text style={styles.chargeName} numberOfLines={1}>{charge.merchant}</Text>
          <Text style={styles.chargeAmount} accessibilityLabel={spokenBRL(charge.amount)}>{formatBRL(charge.amount)}</Text>
        </View>
      ))}
    </View>
  );
}

function RecurringScreen() {
  const queryClient = useQueryClient();
  const { data, isLoading, isError, error, refetch } = useRecurring();
  /** O indicador de "puxar para atualizar" só aparece quando a pessoa puxa (não a cada consulta de fundo). */
  const [pulling, setPulling] = useState(false);
  const pull = async () => {
    const epoch = getSessionEpoch();
    setPulling(true);
    try {
      await refetch();
    } finally {
      if (getSessionEpoch() === epoch) setPulling(false);
    }
  };
  /** O item aberto (um por vez). */
  const [openId, setOpenId] = useState<string | null>(null);
  /** Uma correção por vez. */
  const [busy, setBusy] = useState(false);

  const applyAction = async (item: RecurringItemResponse, action: RecurringAction) => {
    if (busy) return;
    const epoch = getSessionEpoch();
    setBusy(true);
    try {
      await recurringApiClient.setOverride(item.id, action.override);
      // A resposta de uma sessão que já acabou (saiu da conta, trocou de grupo) não mexe em nada.
      if (getSessionEpoch() !== epoch) return;
      setOpenId(null);
      showToastGlobal(overrideDoneMessage(item.name, action.override), 'success');
      await queryClient.invalidateQueries({ queryKey: RECURRING_QUERY_KEY });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      Alert.alert('Não foi possível salvar', getApiErrorMessage(err, 'Tente novamente.'));
    } finally {
      if (getSessionEpoch() === epoch) setBusy(false);
    }
  };

  const sections = data ? recurringSections(data) : [];

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={pulling} onRefresh={() => void pull()} tintColor={colors.primaryLight} />}
      >
        <TouchableOpacity style={styles.backRow} onPress={leave} accessibilityRole="button" accessibilityLabel="Voltar para o Painel">
          <Ionicons name="chevron-back" size={20} color={colors.primaryLight} />
          <Text style={styles.backText}>Painel</Text>
        </TouchableOpacity>
        <Text style={styles.title} accessibilityRole="header">{RECURRING_TITLE}</Text>
        <Text style={styles.subtitle}>O que se repete nos gastos do grupo, encontrado nas transações.</Text>

        {isLoading && <LoadingState />}

        {isError && !isLoading && !data && (
          <ErrorState message={getApiErrorMessage(error, 'Não foi possível carregar as recorrências.')} onRetry={() => void refetch()} />
        )}

        {isError && !!data && <Text style={styles.stale} accessibilityRole="alert">{RECURRING_STALE_TEXT}</Text>}

        {data && !isLoading && isRecurringEmpty(data) && (
          <View style={styles.emptyBox}>
            <Ionicons name="repeat-outline" size={48} color={colors.textMuted} />
            <Text style={styles.emptyText}>{RECURRING_EMPTY_TEXT}</Text>
            <Text style={styles.muted}>Conforme as transações entram, as assinaturas, contas fixas e parcelas aparecem aqui sozinhas.</Text>
            <Text style={styles.muted}>{RECURRING_NOT_LISTED_NOTE}</Text>
          </View>
        )}

        {data && !isLoading && !isRecurringEmpty(data) && (
          <>
            <View style={styles.totals}>
              <View style={styles.totalBox}>
                <Text style={styles.totalLabel}>Por mês</Text>
                <Text style={styles.totalValue} accessibilityLabel={`Total por mês: ${spokenBRL(data.monthlyTotal)}`}>{formatBRL(data.monthlyTotal)}</Text>
              </View>
              <View style={styles.totalBox}>
                <Text style={styles.totalLabel}>Em 12 meses</Text>
                <Text style={styles.totalValue} accessibilityLabel={`Total em 12 meses: ${spokenBRL(data.annualTotal)}`}>{formatBRL(data.annualTotal)}</Text>
              </View>
            </View>
            <Text style={styles.note}>{RECURRING_TOTALS_NOTE}</Text>
            <Text style={styles.note}>{RECURRING_HINT}</Text>
            <Text style={styles.note}>{RECURRING_NOT_LISTED_NOTE}</Text>

            {sections.map((section) => (
              <View key={section.key} style={styles.section}>
                <Text style={styles.sectionTitle} accessibilityRole="header">{section.title}</Text>
                {section.items.map((item) => {
                  const open = openId === item.id;
                  const badges = itemBadges(item);
                  return (
                    <View key={item.id} style={styles.item}>
                      <TouchableOpacity
                        style={styles.itemHeader}
                        onPress={() => setOpenId(open ? null : item.id)}
                        accessibilityRole="button"
                        accessibilityState={{ expanded: open }}
                        accessibilityLabel={itemLabel(item)}
                      >
                        <View style={styles.itemMain}>
                          <Text style={styles.itemName} numberOfLines={2}>{item.name}</Text>
                          <Text style={styles.itemDetail}>{itemDetail(item)}</Text>
                          {badges.length > 0 && (
                            <View style={styles.badges}>
                              {badges.map((badge) => (
                                <Text key={badge} style={styles.badge}>{badge}</Text>
                              ))}
                            </View>
                          )}
                        </View>
                        <View style={styles.itemSide}>
                          <Text style={styles.itemAmount}>{amountText(item)}</Text>
                          <Ionicons name={open ? 'chevron-up' : 'chevron-down'} size={18} color={colors.textMuted} />
                        </View>
                      </TouchableOpacity>

                      {open && (
                        <View style={styles.itemBody}>
                          <Charges id={item.id} />
                          <Text style={styles.chargesTitle}>Está errado?</Text>
                          <View style={styles.actions}>
                            {itemActions(item).map((action) => (
                              <TouchableOpacity
                                key={action.label}
                                style={[styles.actionBtn, busy && styles.btnDisabled]}
                                onPress={() => void applyAction(item, action)}
                                disabled={busy}
                                accessibilityRole="button"
                                accessibilityLabel={action.label}
                              >
                                <Text style={styles.actionText}>{action.label}</Text>
                              </TouchableOpacity>
                            ))}
                          </View>
                        </View>
                      )}
                    </View>
                  );
                })}
              </View>
            ))}
          </>
        )}
      </ScrollView>
    </SafeAreaView>
  );
}

export default resetOnFocus(RecurringScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  backRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 16, minHeight: 44 },
  backText: { fontSize: 16, color: colors.primaryLight, fontWeight: '500' },
  title: { fontSize: 26, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, marginTop: 4, marginBottom: 20 },
  totals: { flexDirection: 'row', gap: 8 },
  totalBox: { flex: 1, backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 16, alignItems: 'center' },
  totalLabel: { fontSize: 12, color: colors.textMuted, marginBottom: 4 },
  totalValue: { fontSize: 20, fontWeight: '800', color: colors.text },
  note: { fontSize: 13, color: colors.textMuted, marginTop: 8, lineHeight: 19 },
  muted: { fontSize: 13, color: colors.textMuted, marginTop: 4, lineHeight: 19 },
  stale: { fontSize: 13, color: colors.warning, marginBottom: 12, lineHeight: 19 },
  emptyBox: { alignItems: 'center', paddingVertical: 40, paddingHorizontal: 12 },
  emptyText: { fontSize: 16, fontWeight: '600', color: colors.text, textAlign: 'center', marginTop: 16, marginBottom: 8, lineHeight: 22 },
  section: { marginTop: 24 },
  sectionTitle: { fontSize: 12, fontWeight: '600', color: colors.textMuted, marginBottom: 8, textTransform: 'uppercase', letterSpacing: 0.5 },
  item: { backgroundColor: colors.surface, borderRadius: 12, borderWidth: 1, borderColor: colors.border, marginBottom: 8 },
  itemHeader: { flexDirection: 'row', alignItems: 'center', padding: 14, minHeight: 56, gap: 8 },
  itemMain: { flex: 1 },
  itemSide: { alignItems: 'flex-end', gap: 4 },
  itemName: { fontSize: 15, fontWeight: '600', color: colors.text },
  itemDetail: { fontSize: 12, color: colors.textMuted, marginTop: 2, lineHeight: 17 },
  itemAmount: { fontSize: 14, fontWeight: '700', color: colors.text },
  badges: { flexDirection: 'row', flexWrap: 'wrap', gap: 6, marginTop: 6 },
  badge: {
    fontSize: 11,
    fontWeight: '600',
    color: colors.warning,
    borderWidth: 1,
    borderColor: colors.warning,
    borderRadius: 8,
    paddingHorizontal: 6,
    paddingVertical: 2,
    overflow: 'hidden',
  },
  itemBody: { paddingHorizontal: 14, paddingBottom: 14, borderTopWidth: 1, borderTopColor: colors.border },
  chargesLoading: { marginTop: 12 },
  chargesTitle: { fontSize: 12, fontWeight: '700', color: colors.textSubtle, marginTop: 12, marginBottom: 4 },
  chargeRow: { flexDirection: 'row', alignItems: 'center', paddingVertical: 6, gap: 8 },
  chargeDate: { fontSize: 13, color: colors.textMuted, width: 84 },
  chargeName: { flex: 1, fontSize: 13, color: colors.textSubtle },
  chargeAmount: { fontSize: 13, fontWeight: '600', color: colors.text },
  retryRow: { minHeight: 44, justifyContent: 'center' },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 4 },
  actionBtn: { minHeight: 44, borderRadius: 10, borderWidth: 1, borderColor: colors.primary, paddingHorizontal: 12, justifyContent: 'center' },
  actionText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  btnDisabled: { opacity: 0.6 },
});
