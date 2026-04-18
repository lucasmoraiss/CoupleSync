// AC-007: Alert settings screen — toggles for low balance, large transaction, bill reminder
import React, { useCallback } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  Switch,
  ActivityIndicator,
  TouchableOpacity,
} from 'react-native';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { notificationsApiClient } from '@/services/apiClient';
import type { NotificationSettingsResponse, UpdateNotificationSettingsRequest } from '@/types/api';
import { colors } from '@/theme';

export default function AlertSettingsScreen() {
  const queryClient = useQueryClient();

  const { data, isLoading, isError, refetch } = useQuery<NotificationSettingsResponse>({
    queryKey: ['notification-settings'],
    queryFn: () => notificationsApiClient.getSettings().then((r) => r.data),
    staleTime: 30_000,
  });

  const { mutate } = useMutation({
    mutationFn: (updates: UpdateNotificationSettingsRequest) =>
      notificationsApiClient.updateSettings(updates),
    onMutate: async (updates) => {
      await queryClient.cancelQueries({ queryKey: ['notification-settings'] });
      const previous = queryClient.getQueryData<NotificationSettingsResponse>(['notification-settings']);
      queryClient.setQueryData<NotificationSettingsResponse>(
        ['notification-settings'],
        (old) => old ? { ...old, ...updates } : old,
      );
      return { previous };
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(['notification-settings'], context.previous);
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['notification-settings'] });
    },
  });

  const handleToggle = useCallback(
    (key: keyof UpdateNotificationSettingsRequest, value: boolean) => {
      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
      mutate({ [key]: value });
    },
    [mutate],
  );

  return (
    <SafeAreaView style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity style={styles.backRow} onPress={() => router.back()}>
          <Ionicons name="chevron-back" size={20} color={colors.primaryLight} />
          <Text style={styles.backText}>Configurações</Text>
        </TouchableOpacity>
        <Text style={styles.title}>Alertas</Text>
        <Text style={styles.subtitle}>Gerencie suas notificações de alerta</Text>
      </View>

      {isLoading && (
        <View style={styles.centered}>
          <ActivityIndicator color={colors.primary} />
        </View>
      )}

      {isError && (
        <View style={styles.centered}>
          <Text style={styles.errorText}>Não foi possível carregar as configurações.</Text>
          <TouchableOpacity onPress={() => refetch()} style={styles.retryButton}>
            <Text style={styles.retryText}>Tentar novamente</Text>
          </TouchableOpacity>
        </View>
      )}

      {data && (
        <View style={styles.section}>
          {/* Low balance */}
          <View style={styles.row}>
            <View style={styles.rowInfo}>
              <Text style={styles.rowTitle}>Saldo baixo</Text>
              <Text style={styles.rowDesc}>Avisa quando o saldo estiver baixo</Text>
            </View>
            <Switch
              value={data.lowBalanceEnabled}
              onValueChange={(v) => handleToggle('lowBalanceEnabled', v)}
              trackColor={{ false: colors.border, true: colors.primary }}
              thumbColor={data.lowBalanceEnabled ? colors.text : colors.textMuted}
            />
          </View>
          <View style={styles.divider} />

          {/* Large transaction */}
          <View style={styles.row}>
            <View style={styles.rowInfo}>
              <Text style={styles.rowTitle}>Transação grande</Text>
              <Text style={styles.rowDesc}>Notifica ao detectar transações de alto valor</Text>
            </View>
            <Switch
              value={data.largeTransactionEnabled}
              onValueChange={(v) => handleToggle('largeTransactionEnabled', v)}
              trackColor={{ false: colors.border, true: colors.primary }}
              thumbColor={data.largeTransactionEnabled ? colors.text : colors.textMuted}
            />
          </View>
          <View style={styles.divider} />

          {/* Bill reminder */}
          <View style={styles.row}>
            <View style={styles.rowInfo}>
              <Text style={styles.rowTitle}>Lembretes de contas</Text>
              <Text style={styles.rowDesc}>Lembra de boletos e contas a pagar</Text>
            </View>
            <Switch
              value={data.billReminderEnabled}
              onValueChange={(v) => handleToggle('billReminderEnabled', v)}
              trackColor={{ false: colors.border, true: colors.primary }}
              thumbColor={data.billReminderEnabled ? colors.text : colors.textMuted}
            />
          </View>
        </View>
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background, paddingHorizontal: 20, paddingTop: 24 },
  header: { marginBottom: 28 },
  backRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 16 },
  backText: { fontSize: 16, color: colors.primaryLight, fontWeight: '500' },
  title: { fontSize: 26, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, marginTop: 4 },
  centered: { flex: 1, justifyContent: 'center', alignItems: 'center' },
  retryButton: { marginTop: 12, paddingHorizontal: 24, paddingVertical: 10, backgroundColor: colors.primary, borderRadius: 10 },
  retryText: { color: colors.text, fontWeight: '600', fontSize: 14 },
  errorText: { fontSize: 16, color: colors.errorLight },
  section: {
    backgroundColor: colors.surface,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: colors.border,
    overflow: 'hidden',
  },
  row: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingHorizontal: 20,
    paddingVertical: 18,
  },
  rowInfo: { flex: 1, marginRight: 16 },
  rowTitle: { fontSize: 16, color: colors.text, fontWeight: '500' },
  rowDesc: { fontSize: 13, color: colors.textMuted, marginTop: 3 },
  divider: { height: 1, backgroundColor: colors.border, marginHorizontal: 20 },
});
