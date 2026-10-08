// Configurações > Inteligência artificial (aba oculta): se a análise com IA está ativada para o grupo e por quem,
// ativar, desligar para o grupo, retirar o próprio aceite, o consumo (hoje e 30 dias; a cota de cada modelo quando
// é conhecida; o orçamento do grupo) e o caminho para o texto de privacidade. Tudo vem do servidor e é consultado
// de novo a cada visita.
import React, { useState } from 'react';
import { View, Text, StyleSheet, SafeAreaView, ScrollView, TouchableOpacity, Alert, ActivityIndicator } from 'react-native';
import { router } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import { colors } from '@/theme';
import { aiApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { LoadingState } from '@/components/LoadingState';
import { ErrorState } from '@/components/ErrorState';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { goToParent, useOnRefocus } from '@/navigation/resetOnFocus';
import { formatConsentDate } from '@/modules/privacy/consent';
import { activationSummary, groupBudgetText, modelQuotaText, usageTotals } from '@/modules/ai/aiStatus';
import { useAiStatusStore } from '@/modules/ai/aiStatusStore';
import { useAiStatus } from '@/modules/ai/useAiStatus';
import type { AiUsageResponse } from '@/types/api';

const USAGE_DAYS = 30;
const AI_USAGE_QUERY_KEY = ['ai-usage', USAGE_DAYS] as const;

function usageLine(label: string, total: { calls: number; tokens: number; failures: number }): string {
  const failures = total.failures > 0 ? ` · ${total.failures} sem resposta` : '';
  return `${label}: ${total.calls} ${total.calls === 1 ? 'chamada' : 'chamadas'} · ${total.tokens.toLocaleString('pt-BR')} tokens${failures}`;
}

function UsageSection({ enabled }: { enabled: boolean }) {
  const { data, isLoading, isError, refetch } = useQuery<AiUsageResponse>({
    queryKey: AI_USAGE_QUERY_KEY,
    queryFn: async () => (await aiApiClient.getUsage(USAGE_DAYS)).data,
    enabled,
    retry: 1,
  });
  useOnRefocus(() => {
    if (enabled) void refetch();
  });

  if (!enabled) return null;

  return (
    <View style={styles.box}>
      <Text style={styles.boxTitle} accessibilityRole="header">Consumo do grupo</Text>
      {isLoading && <ActivityIndicator color={colors.primary} accessibilityLabel="Carregando o consumo" />}
      {isError && !isLoading && (
        <TouchableOpacity onPress={() => void refetch()} accessibilityRole="button" accessibilityLabel="Não foi possível carregar o consumo. Tentar de novo" style={styles.retryRow}>
          <Text style={styles.muted}>Não foi possível carregar o consumo. Toque para tentar de novo.</Text>
        </TouchableOpacity>
      )}
      {data && !isLoading && (
        <>
          <Text style={styles.line}>{groupBudgetText(data.groupBudget)}</Text>
          <Text style={styles.muted}>O limite do grupo volta à meia-noite (horário de Brasília).</Text>
          <Text style={styles.line}>{usageLine('Hoje', usageTotals(data.days).today)}</Text>
          <Text style={styles.line}>{usageLine(`Últimos ${USAGE_DAYS} dias`, usageTotals(data.days).period)}</Text>
          {data.providersToday.length > 0 && <Text style={styles.subTitle}>Modelos (hoje)</Text>}
          {data.providersToday.map((provider) => (
            <View key={`${provider.name}|${provider.model}`} style={styles.modelRow}>
              <Text style={styles.modelName}>{provider.model}</Text>
              <Text style={styles.muted}>{modelQuotaText(provider)}</Text>
            </View>
          ))}
        </>
      )}
    </View>
  );
}

export default function AiSettingsScreen() {
  const { status, loadFailed, refresh } = useAiStatus();
  const [busy, setBusy] = useState(false);

  const change = async (work: () => Promise<unknown>, doneMessage: string) => {
    if (busy) return;
    setBusy(true);
    try {
      await work();
      showToastGlobal(doneMessage, 'success');
    } catch (err) {
      Alert.alert('Não foi possível salvar', getApiErrorMessage(err, 'Tente novamente.'));
    } finally {
      setBusy(false);
    }
  };

  const confirmGroupOff = () => {
    Alert.alert(
      'Desligar para o grupo?',
      'Nada mais será enviado à IA por ninguém do grupo. O Assistente para de responder; os números do app continuam iguais. Dá para ativar de novo quando quiserem.',
      [
        { text: 'Cancelar', style: 'cancel' },
        {
          text: 'Desligar',
          style: 'destructive',
          onPress: () => void change(() => useAiStatusStore.getState().revoke('group'), 'Análise com IA desligada para o grupo.'),
        },
      ],
    );
  };

  const withdrawMine = () => {
    void change(() => useAiStatusStore.getState().revoke('mine'), 'O seu aceite foi retirado.');
  };

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <TouchableOpacity
          style={styles.backRow}
          onPress={() => goToParent('settings/ai')}
          accessibilityRole="button"
          accessibilityLabel="Voltar para as configurações"
        >
          <Ionicons name="chevron-back" size={20} color={colors.primaryLight} />
          <Text style={styles.backText}>Configurações</Text>
        </TouchableOpacity>
        <Text style={styles.title} accessibilityRole="header">Inteligência artificial</Text>
        <Text style={styles.subtitle}>Análise das finanças do grupo com IA e o Assistente.</Text>

        {!status && !loadFailed && <LoadingState />}
        {!status && loadFailed && (
          <ErrorState message="Não foi possível carregar. Verifique a internet e tente novamente." onRetry={() => void refresh()} />
        )}

        {status && (
          <>
            <View style={styles.box}>
              <Text style={styles.boxTitle} accessibilityRole="header">Análise com IA</Text>
              <Text style={styles.state}>{activationSummary(status, formatConsentDate)}</Text>

              {!status.available && (
                <Text style={styles.muted}>A análise com IA não está disponível no momento. O restante do app funciona normalmente.</Text>
              )}

              {status.available && !status.enabled && (
                <>
                  <Text style={styles.muted}>Uma pessoa ativa e vale para o grupo inteiro. Antes de ativar você vê o que é enviado e para onde.</Text>
                  <TouchableOpacity
                    style={styles.primaryBtn}
                    onPress={() => router.push('/(main)/ai/welcome' as any)}
                    accessibilityRole="button"
                    accessibilityLabel="Ativar a análise com IA: ver o que é enviado e decidir"
                  >
                    <Text style={styles.primaryText}>Ativar para o grupo</Text>
                  </TouchableOpacity>
                </>
              )}

              {status.enabled && (
                <>
                  <TouchableOpacity
                    style={[styles.dangerBtn, busy && styles.btnDisabled]}
                    onPress={confirmGroupOff}
                    disabled={busy}
                    accessibilityRole="button"
                    accessibilityLabel="Desligar a análise com IA para o grupo"
                  >
                    <Text style={styles.dangerText}>Desligar para o grupo</Text>
                  </TouchableOpacity>
                  {status.myAcceptance !== null && (
                    <>
                      <TouchableOpacity
                        style={[styles.secondaryBtn, busy && styles.btnDisabled]}
                        onPress={withdrawMine}
                        disabled={busy}
                        accessibilityRole="button"
                        accessibilityLabel="Retirar o meu aceite da análise com IA"
                      >
                        <Text style={styles.secondaryText}>Retirar meu aceite</Text>
                      </TouchableOpacity>
                      <Text style={styles.muted}>
                        Retirar o seu aceite só desliga a análise se ninguém mais do grupo tiver ativado.
                      </Text>
                    </>
                  )}
                </>
              )}
            </View>

            <UsageSection enabled={status.available} />

            <TouchableOpacity
              style={styles.linkRow}
              onPress={() => router.push('/(main)/settings/privacy' as any)}
              accessibilityRole="button"
              accessibilityLabel="Abrir a privacidade: o que é enviado à IA e para onde"
            >
              <Text style={styles.linkText}>Privacidade: o que é enviado e para onde</Text>
              <Ionicons name="chevron-forward" size={18} color={colors.primaryLight} />
            </TouchableOpacity>
          </>
        )}
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  backRow: { flexDirection: 'row', alignItems: 'center', marginBottom: 16, minHeight: 44 },
  backText: { fontSize: 16, color: colors.primaryLight, fontWeight: '500' },
  title: { fontSize: 26, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, marginTop: 4, marginBottom: 24 },
  box: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 16, marginBottom: 16 },
  boxTitle: { fontSize: 15, fontWeight: '700', color: colors.text, marginBottom: 8 },
  subTitle: { fontSize: 13, fontWeight: '700', color: colors.textSubtle, marginTop: 12, marginBottom: 4 },
  state: { fontSize: 16, color: colors.text, fontWeight: '600', marginBottom: 8, lineHeight: 22 },
  line: { fontSize: 14, color: colors.textSubtle, marginTop: 6, lineHeight: 20 },
  muted: { fontSize: 13, color: colors.textMuted, marginTop: 4, lineHeight: 19 },
  retryRow: { minHeight: 44, justifyContent: 'center' },
  modelRow: { marginTop: 6 },
  modelName: { fontSize: 14, color: colors.text, fontWeight: '500' },
  primaryBtn: { minHeight: 48, borderRadius: 12, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center', marginTop: 12 },
  primaryText: { color: colors.text, fontSize: 16, fontWeight: '600' },
  dangerBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.error, alignItems: 'center', justifyContent: 'center', marginTop: 8 },
  dangerText: { color: colors.errorLight, fontSize: 16, fontWeight: '600' },
  secondaryBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center', marginTop: 8 },
  secondaryText: { color: colors.textSubtle, fontSize: 16, fontWeight: '600' },
  btnDisabled: { opacity: 0.6 },
  linkRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', minHeight: 48, paddingHorizontal: 4 },
  linkText: { fontSize: 15, color: colors.primaryLight, fontWeight: '600' },
});
