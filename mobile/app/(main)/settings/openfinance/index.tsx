// Open Finance: o que o grupo conectou pelo Meu Pluggy. Todos do grupo veem bancos, contas e saldos; só quem
// conectou liga/desliga a sincronização de cada conta, sincroniza, adiciona banco e desconecta.
import React, { useCallback, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Switch,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { router } from 'expo-router';
import { useQueryClient } from '@tanstack/react-query';
import { openFinanceApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { getSessionEpoch } from '@/state/sessionStore';
import { ErrorState } from '@/components/ErrorState';
import { LoadingState } from '@/components/LoadingState';
import { goToParent, useOnRefocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';
import { OPEN_FINANCE_STATUS_KEY, useOpenFinanceStatus } from '@/modules/openfinance/useOpenFinanceStatus';
import { BANK_REVIEW_KEY } from '@/modules/openfinance/useBankReview';
import { useSyncRun } from '@/modules/openfinance/useSyncRun';
import {
  CHOOSE_PERIOD_BUTTON,
  CHOOSE_PERIOD_TEXT,
  SYNC_STILL_RUNNING_TEXT,
  lastSyncText,
  runProgressText,
  runResultText,
  shouldClearSyncOnRefocus,
  syncActionOf,
} from '@/modules/openfinance/sync';
import { aiUploadConsent } from '@/modules/ai/aiStatus';
import { currentAiStatus } from '@/modules/ai/aiStatusStore';
import { useWizardStore } from '@/modules/openfinance/wizardStore';
import {
  UNAVAILABLE_TEXT,
  UNAVAILABLE_TITLE,
  accountBalanceText,
  connectionControls,
  connectionErrorHelp,
  connectionStatusLabel,
  describeAccount,
  isOpenFinanceAvailable,
  itemStatusNote,
  myConnectionOf,
} from '@/modules/openfinance/wizard';
import type { BankAccountResponse, BankConnectionResponse, OpenFinanceStatusResponse } from '@/types/api';

function formatMoney(value: number, currency: string): string {
  try {
    return new Intl.NumberFormat('pt-BR', { style: 'currency', currency: currency || 'BRL' }).format(value);
  } catch {
    // Moeda que o aparelho não conhece: o número com o código da moeda.
    return `${currency} ${value.toFixed(2).replace('.', ',')}`;
  }
}

function openWizard() {
  router.push('/(main)/settings/openfinance/wizard' as any);
}

export default function OpenFinanceScreen() {
  const queryClient = useQueryClient();
  const { data, isLoading, isError, refetch } = useOpenFinanceStatus();
  const [refreshing, setRefreshing] = useState(false);
  const [busyAccountId, setBusyAccountId] = useState<string | null>(null);
  const [disconnecting, setDisconnecting] = useState(false);
  const sync = useSyncRun();

  // "Sincronizar agora": pede ao Pluggy para ler os bancos de novo e acompanha a sincronização do servidor.
  const syncNow = async (connection: BankConnectionResponse) => {
    if (sync.phase === 'working') return;
    const epoch = getSessionEpoch();
    const run = await sync.start(connection.id, {
      force: true,
      aiConsent: aiUploadConsent(currentAiStatus()),
    });
    if (getSessionEpoch() !== epoch || !run) return;
    void queryClient.invalidateQueries({ queryKey: BANK_REVIEW_KEY });
    await refetch({ cancelRefetch: false });
  };

  // Primeira sincronização: a pessoa escolhe antes quanto do passado trazer, no passo "Período" do wizard (que
  // abre nele quando o progresso guardado aponta para esta conexão).
  const choosePeriod = async (connection: BankConnectionResponse) => {
    const epoch = getSessionEpoch();
    const store = useWizardStore.getState();
    await store.load();
    if (getSessionEpoch() !== epoch) return;
    await store.save({ step: 5, connectionId: connection.id });
    if (getSessionEpoch() !== epoch) return;
    openWizard();
  };

  // A tela é uma aba oculta e continua montada: o que o parceiro conectou, o status e os saldos mudam sem ela
  // saber. Busca de novo a cada volta à tela e ao puxar para atualizar. O resultado da sincronização pedida numa
  // visita anterior ("concluída…", "sincronizada há pouco…") não fica para esta.
  useOnRefocus(() => {
    if (shouldClearSyncOnRefocus(sync.phase)) sync.reset();
    void refetch({ cancelRefetch: false });
  });
  const handleRefresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await refetch({ cancelRefetch: false });
    } finally {
      setRefreshing(false);
    }
  }, [refetch]);

  const toggleSync = async (account: BankAccountResponse, syncEnabled: boolean) => {
    if (busyAccountId) return;
    const epoch = getSessionEpoch();
    setBusyAccountId(account.id);
    try {
      const { data: updated } = await openFinanceApiClient.setAccountSync(account.id, syncEnabled);
      // Saiu da conta ou trocou de grupo enquanto esperava: a resposta é de outra sessão.
      if (getSessionEpoch() !== epoch) return;
      queryClient.setQueryData<OpenFinanceStatusResponse>(OPEN_FINANCE_STATUS_KEY, (old) =>
        old
          ? {
              ...old,
              connections: old.connections.map((connection) => ({
                ...connection,
                items: connection.items.map((item) => ({
                  ...item,
                  accounts: item.accounts.map((a) => (a.id === updated.id ? updated : a)),
                })),
              })),
            }
          : old,
      );
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      Alert.alert('Não foi possível alterar', getApiErrorMessage(err, 'Erro ao mudar a sincronização desta conta. Tente novamente.'));
    } finally {
      if (getSessionEpoch() === epoch) setBusyAccountId(null);
    }
  };

  const disconnect = async (connection: BankConnectionResponse) => {
    const epoch = getSessionEpoch();
    setDisconnecting(true);
    try {
      await openFinanceApiClient.disconnect(connection.id);
      if (getSessionEpoch() !== epoch) return;
      // O progresso guardado apontava para a conexão que deixou de valer.
      await useWizardStore.getState().finish();
      if (getSessionEpoch() !== epoch) return;
      await refetch({ cancelRefetch: false });
    } catch (err) {
      if (getSessionEpoch() !== epoch) return;
      Alert.alert('Não foi possível desconectar', getApiErrorMessage(err, 'Erro ao desconectar. Tente novamente.'));
    } finally {
      if (getSessionEpoch() === epoch) setDisconnecting(false);
    }
  };

  const confirmDisconnect = (connection: BankConnectionResponse) => {
    Alert.alert(
      'Desconectar',
      'O Client ID e o Client Secret são apagados do servidor agora. Os bancos e as contas já encontrados continuam visíveis para o grupo. Para voltar a usar, será preciso informar as credenciais de novo.',
      [
        { text: 'Cancelar', style: 'cancel' },
        { text: 'Desconectar', style: 'destructive', onPress: () => void disconnect(connection) },
      ],
    );
  };

  const header = (
    <>
      <TouchableOpacity onPress={() => goToParent('settings/openfinance/index')} accessibilityRole="button" accessibilityLabel="Voltar para as configurações">
        <Text style={styles.back}>← Voltar</Text>
      </TouchableOpacity>
      <Text style={styles.title} accessibilityRole="header">Open Finance</Text>
    </>
  );

  if (isLoading) return <LoadingState />;
  if (isError || !data) {
    return (
      <SafeAreaView style={styles.container}>
        <View style={styles.content}>{header}</View>
        <ErrorState message="Não foi possível carregar o Open Finance." onRetry={() => refetch()} />
      </SafeAreaView>
    );
  }

  const available = isOpenFinanceAvailable(data);
  const mine = myConnectionOf(data);
  const connections = data.connections ?? [];

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={handleRefresh} tintColor={colors.primaryLight} />}
      >
        {header}
        <Text style={styles.subtitle}>
          Conecte os seus bancos pelo Meu Pluggy para o CoupleSync ver as suas contas e cartões. Tudo o que for conectado aparece para o grupo inteiro.
        </Text>

        {!available ? (
          <View style={styles.card} accessibilityRole="alert">
            <Text style={styles.cardTitle}>{UNAVAILABLE_TITLE}</Text>
            <Text style={styles.cardText}>{UNAVAILABLE_TEXT}</Text>
          </View>
        ) : null}

        {available && !mine ? (
          <View style={styles.card}>
            <Text style={styles.cardTitle}>Você ainda não conectou</Text>
            <Text style={styles.cardText}>
              São 5 passos, com os links e as instruções de cada um. Você vai precisar de uma conta gratuita no Meu Pluggy.
            </Text>
            <TouchableOpacity style={styles.primaryBtn} onPress={openWizard} accessibilityRole="button" accessibilityLabel="Conectar meus bancos pelo Meu Pluggy">
              <Text style={styles.primaryText}>Conectar meus bancos</Text>
            </TouchableOpacity>
          </View>
        ) : null}

        {connections.map((connection) => {
          const controls = connectionControls(connection, available);
          const errorHelp = connectionErrorHelp(connection);
          const syncAction = syncActionOf(connection, available);
          return (
            <View key={connection.id} style={styles.card}>
              <Text style={styles.cardTitle}>{connection.label}</Text>
              <Text style={styles.cardText}>
                {connection.isMine ? 'Conectada por você' : `Conectada por ${connection.userName}`} · {connectionStatusLabel(connection.status)}
              </Text>
              {connection.clientIdHint ? <Text style={styles.muted}>Client ID ····{connection.clientIdHint}</Text> : null}
              <Text style={styles.muted}>{lastSyncText(connection)}</Text>
              {connection.lastErrorMessage ? (
                <Text style={styles.errorText} accessibilityRole="alert">{connection.lastErrorMessage}</Text>
              ) : null}
              {errorHelp ? <Text style={styles.cardText}>{errorHelp}</Text> : null}

              {connection.items.length === 0 ? (
                <Text style={styles.muted}>Nenhum banco verificado nesta conexão.</Text>
              ) : null}

              {connection.items.map((item) => {
                const note = itemStatusNote(item.status);
                return (
                  <View key={item.id} style={styles.item}>
                    <Text style={styles.itemTitle} accessibilityRole="header">{item.connectorName || 'Banco'}</Text>
                    {note ? <Text style={styles.warningText}>{note}</Text> : null}
                    {item.accounts.map((account) => (
                      <View key={account.id} style={styles.accountRow}>
                        <View style={styles.accountInfo}>
                          <Text style={styles.accountName}>{account.name || account.marketingName || 'Conta'}</Text>
                          <Text style={styles.muted}>{describeAccount(account)}</Text>
                          <Text style={styles.muted}>{accountBalanceText(account, formatMoney)}</Text>
                          {!controls.syncSwitch ? (
                            <Text style={styles.muted}>{account.syncEnabled ? 'Sincroniza' : 'Não sincroniza'}</Text>
                          ) : null}
                        </View>
                        {controls.syncSwitch ? (
                          <View style={styles.switchBox}>
                            <Text style={styles.switchLabel}>sincronizar</Text>
                            <Switch
                              value={account.syncEnabled}
                              onValueChange={(value) => void toggleSync(account, value)}
                              disabled={!available || busyAccountId !== null || disconnecting}
                              trackColor={{ false: colors.border, true: colors.primary }}
                              thumbColor={account.syncEnabled ? colors.text : colors.textMuted}
                              accessibilityRole="switch"
                              accessibilityLabel={`Sincronizar ${account.name || 'conta'} ${describeAccount(account)}`}
                              accessibilityState={{ checked: account.syncEnabled, disabled: !available || busyAccountId !== null || disconnecting }}
                            />
                          </View>
                        ) : null}
                      </View>
                    ))}
                  </View>
                );
              })}

              {syncAction === 'choosePeriod' ? (
                <View style={styles.syncBox}>
                  <Text style={styles.cardText}>{CHOOSE_PERIOD_TEXT}</Text>
                  <TouchableOpacity
                    style={[styles.primaryBtn, disconnecting && styles.disabledBtn]}
                    onPress={() => void choosePeriod(connection)}
                    disabled={disconnecting}
                    accessibilityRole="button"
                    accessibilityLabel={CHOOSE_PERIOD_BUTTON}
                    accessibilityState={{ disabled: disconnecting }}
                  >
                    <Text style={styles.primaryText}>{CHOOSE_PERIOD_BUTTON}</Text>
                  </TouchableOpacity>
                </View>
              ) : null}

              {syncAction === 'syncNow' ? (
                <View style={styles.syncBox}>
                  <TouchableOpacity
                    style={[styles.primaryBtn, (sync.phase === 'working' || disconnecting) && styles.disabledBtn]}
                    onPress={() => void syncNow(connection)}
                    disabled={sync.phase === 'working' || disconnecting}
                    accessibilityRole="button"
                    accessibilityLabel="Sincronizar agora com os bancos"
                    accessibilityState={{ disabled: sync.phase === 'working' || disconnecting, busy: sync.phase === 'working' }}
                  >
                    {sync.phase === 'working' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Sincronizar agora</Text>}
                  </TouchableOpacity>
                  {sync.phase === 'working' ? (
                    <Text style={styles.muted} accessibilityLiveRegion="polite">{runProgressText(sync.run)}</Text>
                  ) : null}
                  {sync.phase === 'done' && sync.run ? (
                    <Text style={styles.cardText} accessibilityRole="alert" accessibilityLiveRegion="polite">{runResultText(sync.run)}</Text>
                  ) : null}
                  {sync.phase === 'failed' ? (
                    <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">
                      {sync.run ? runResultText(sync.run) : sync.requestError}
                    </Text>
                  ) : null}
                  {sync.phase === 'stillRunning' ? (
                    <Text style={styles.cardText} accessibilityRole="alert" accessibilityLiveRegion="polite">{SYNC_STILL_RUNNING_TEXT}</Text>
                  ) : null}
                </View>
              ) : null}

              {controls.addBank || controls.reconnect ? (
                <View style={styles.actions}>
                  <TouchableOpacity
                    style={styles.secondaryBtn}
                    onPress={openWizard}
                    disabled={disconnecting}
                    accessibilityRole="button"
                    accessibilityLabel={controls.reconnect ? 'Conectar de novo com as credenciais do Pluggy' : 'Adicionar banco a esta conexão'}
                  >
                    <Text style={styles.secondaryText}>{controls.reconnect ? 'Conectar de novo' : 'Adicionar banco'}</Text>
                  </TouchableOpacity>
                  {controls.disconnect ? (
                    <TouchableOpacity
                      style={styles.dangerBtn}
                      onPress={() => confirmDisconnect(connection)}
                      disabled={disconnecting}
                      accessibilityRole="button"
                      accessibilityLabel="Desconectar: apagar as credenciais do servidor"
                      accessibilityState={{ disabled: disconnecting, busy: disconnecting }}
                    >
                      {disconnecting ? <ActivityIndicator color={colors.errorLight} /> : <Text style={styles.dangerText}>Desconectar</Text>}
                    </TouchableOpacity>
                  ) : null}
                </View>
              ) : null}
            </View>
          );
        })}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 15, marginBottom: 16 },
  title: { fontSize: 26, fontWeight: '700', color: colors.text, marginBottom: 8 },
  subtitle: { fontSize: 14, color: colors.textMuted, marginBottom: 20, lineHeight: 20 },
  card: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 16, marginBottom: 16, gap: 6 },
  cardTitle: { fontSize: 17, fontWeight: '700', color: colors.text },
  cardText: { fontSize: 14, color: colors.textSubtle, lineHeight: 20 },
  muted: { fontSize: 13, color: colors.textMuted, lineHeight: 19 },
  errorText: { fontSize: 14, color: colors.errorLight, lineHeight: 20 },
  warningText: { fontSize: 13, color: colors.warning, lineHeight: 19 },
  item: { marginTop: 10, paddingTop: 10, borderTopWidth: 1, borderTopColor: colors.border, gap: 6 },
  itemTitle: { fontSize: 15, fontWeight: '700', color: colors.text },
  accountRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingVertical: 6, minHeight: 56 },
  accountInfo: { flex: 1, marginRight: 12 },
  accountName: { fontSize: 15, color: colors.text, fontWeight: '500' },
  switchBox: { alignItems: 'center' },
  switchLabel: { fontSize: 11, color: colors.textMuted, marginBottom: 2 },
  actions: { marginTop: 12, gap: 10 },
  syncBox: { marginTop: 12, gap: 8 },
  primaryBtn: { minHeight: 48, borderRadius: 12, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center', marginTop: 8 },
  primaryText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  disabledBtn: { opacity: 0.5 },
  secondaryBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  secondaryText: { color: colors.textSubtle, fontSize: 16, fontWeight: '600' },
  dangerBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.error, alignItems: 'center', justifyContent: 'center' },
  dangerText: { color: colors.errorLight, fontSize: 16, fontWeight: '600' },
});
