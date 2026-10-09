// Boas-vindas da análise com IA (aba oculta). Uma pergunta só, a do servidor: "Ativar para o grupo" ou "Agora não".
// Se outra pessoa do grupo já ativou, vira um aviso ("X ativou") com "Entendi" e "Desligar para o grupo".
// Abre depois de criar ou entrar num grupo e, uma vez, para quem já usa o app (o Painel traz para cá quando
// GET /ai/status diz `onboardingPending`). As respostas ficam no servidor: não voltam em outro aparelho.
// O estado da tela é descartado a cada visita (resetOnFocus).
import React, { useEffect, useState } from 'react';
import { View, Text, StyleSheet, SafeAreaView, ScrollView, TouchableOpacity, ActivityIndicator } from 'react-native';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { colors, spacing, typography, borderRadius } from '@/theme';
import { getApiErrorMessage } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { aiAnalysisPoints, aiAnalysisSummary } from '@/modules/privacy/privacyContent';
import { aiDestinations, welcomeView } from '@/modules/ai/aiStatus';
import { useAiStatusStore } from '@/modules/ai/aiStatusStore';
import { useAiStatus } from '@/modules/ai/useAiStatus';

// O que a IA faz hoje e o que chega nas próximas atualizações: ninguém ativa por uma promessa que ainda não existe.
const WHAT_IT_DOES: readonly string[] = [
  'Responde às suas perguntas sobre gastos, orçamento e metas, no Assistente.',
  'Em breve: acha assinaturas esquecidas e avisa o que vence e o que mudou.',
  'Em breve: resume a semana e o mês.',
];

type Action = 'activate' | 'dismiss' | 'revoke';

function AiWelcomeScreen() {
  const { status, loadFailed } = useAiStatus();
  const userId = useSessionStore((state) => state.userId);
  const [busy, setBusy] = useState<Action | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(false);

  // Esta abertura conta como "mostrada": o Painel não traz a pessoa de volta para cá a cada foco.
  useEffect(() => {
    useAiStatusStore.getState().markWelcomeShown();
  }, []);

  const view = status ? welcomeView(status, userId) : null;
  // Nada a perguntar (IA indisponível, a pessoa já ativou) ou o status não carregou: Painel.
  const leave = (view !== null && (view.kind === 'unavailable' || view.kind === 'done')) || (status === null && loadFailed);
  useEffect(() => {
    if (leave && busy === null) goToParent('ai/welcome');
  }, [leave, busy]);

  const run = async (action: Action, work: () => Promise<unknown>, doneMessage?: string) => {
    if (busy) return;
    setBusy(action);
    setError(null);
    try {
      await work();
      if (doneMessage) showToastGlobal(doneMessage, 'success');
      goToParent('ai/welcome');
    } catch (err) {
      setError(getApiErrorMessage(err, 'Não foi possível registrar a sua resposta. Tente novamente.'));
      setBusy(null);
    }
  };

  const store = useAiStatusStore.getState();
  const activate = () => run('activate', store.activate, 'Análise com IA ativada para o grupo.');
  const dismiss = () => run('dismiss', store.answerOnboarding);
  const revoke = () => run('revoke', () => store.revoke('group'), 'Análise com IA desligada para o grupo.');

  if (!view || leave) {
    return (
      <SafeAreaView style={[styles.container, styles.centered]}>
        <ActivityIndicator color={colors.primary} accessibilityLabel="Carregando" />
      </SafeAreaView>
    );
  }

  const activatedByOther = view.kind === 'activated-by-other';
  // Para onde os dados vão: o que o servidor tem ligado agora (o texto que a pessoa aceita).
  const destinations = aiDestinations(status);

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <View style={styles.iconCircle} accessible={false} importantForAccessibility="no-hide-descendants">
          <Ionicons name="sparkles" size={28} color={colors.primaryLight} />
        </View>

        <Text style={styles.title} accessibilityRole="header">
          {activatedByOther ? `${view.names} ativou a análise com IA para o grupo` : 'Quer que a IA analise as finanças do grupo?'}
        </Text>
        {activatedByOther && (
          <Text style={styles.lead}>
            Vale para o grupo inteiro. Você pode desligar agora ou a qualquer hora em Configurações {'>'} Inteligência artificial.
          </Text>
        )}

        <View style={styles.list}>
          {WHAT_IT_DOES.map((line) => (
            <View key={line} style={styles.listRow}>
              <Ionicons name="checkmark-circle-outline" size={20} color={colors.primaryLight} style={styles.listIcon} />
              <Text style={styles.listText}>{line}</Text>
            </View>
          ))}
        </View>

        <Text style={styles.summary}>{aiAnalysisSummary(destinations)}</Text>
        <TouchableOpacity
          style={styles.readAll}
          onPress={() => setShowAll((shown) => !shown)}
          accessibilityRole="button"
          accessibilityState={{ expanded: showAll }}
          accessibilityLabel={showAll ? 'Esconder o texto completo sobre a análise com IA' : 'Ler tudo sobre a análise com IA'}
        >
          <Text style={styles.readAllText}>{showAll ? 'Esconder' : 'Ler tudo'}</Text>
        </TouchableOpacity>
        {showAll && (
          <View style={styles.allBox}>
            {aiAnalysisPoints(destinations).map((point) => (
              <Text key={point} style={styles.allText}>{point}</Text>
            ))}
          </View>
        )}
      </ScrollView>

      <View style={styles.actions}>
        {error !== null && (
          <Text style={styles.error} accessibilityRole="alert" accessibilityLiveRegion="assertive">{error}</Text>
        )}
        {activatedByOther ? (
          <>
            <TouchableOpacity
              style={[styles.primaryBtn, busy !== null && styles.btnDisabled]}
              onPress={dismiss}
              disabled={busy !== null}
              accessibilityRole="button"
              accessibilityLabel="Entendi, manter a análise com IA ativada"
            >
              {busy === 'dismiss' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Entendi</Text>}
            </TouchableOpacity>
            <TouchableOpacity
              style={[styles.secondaryBtn, busy !== null && styles.btnDisabled]}
              onPress={revoke}
              disabled={busy !== null}
              accessibilityRole="button"
              accessibilityLabel="Desligar a análise com IA para o grupo"
            >
              {busy === 'revoke' ? <ActivityIndicator color={colors.textSubtle} /> : <Text style={styles.secondaryText}>Desligar para o grupo</Text>}
            </TouchableOpacity>
          </>
        ) : (
          <>
            <TouchableOpacity
              style={[styles.primaryBtn, busy !== null && styles.btnDisabled]}
              onPress={activate}
              disabled={busy !== null}
              accessibilityRole="button"
              accessibilityLabel="Ativar a análise com IA para o grupo"
            >
              {busy === 'activate' ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Ativar para o grupo</Text>}
            </TouchableOpacity>
            <TouchableOpacity
              style={[styles.secondaryBtn, busy !== null && styles.btnDisabled]}
              onPress={dismiss}
              disabled={busy !== null}
              accessibilityRole="button"
              accessibilityLabel="Agora não ativar a análise com IA"
            >
              {busy === 'dismiss' ? <ActivityIndicator color={colors.textSubtle} /> : <Text style={styles.secondaryText}>Agora não</Text>}
            </TouchableOpacity>
          </>
        )}
      </View>
    </SafeAreaView>
  );
}

export default resetOnFocus(AiWelcomeScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  centered: { justifyContent: 'center', alignItems: 'center' },
  content: { paddingHorizontal: spacing.lg, paddingTop: spacing.xl, paddingBottom: spacing.lg },
  iconCircle: {
    width: 56,
    height: 56,
    borderRadius: borderRadius.full,
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: spacing.md,
  },
  title: { fontSize: typography.fontSize.xxl, fontWeight: typography.fontWeight.semibold, color: colors.text, marginBottom: spacing.md },
  lead: { fontSize: typography.fontSize.md, color: colors.textSubtle, lineHeight: 21, marginBottom: spacing.md },
  list: { marginBottom: spacing.md },
  listRow: { flexDirection: 'row', alignItems: 'flex-start', marginBottom: spacing.sm },
  listIcon: { marginRight: spacing.sm, marginTop: 1 },
  listText: { flex: 1, fontSize: typography.fontSize.lg, color: colors.text, lineHeight: 22 },
  summary: { fontSize: typography.fontSize.md, color: colors.textSubtle, lineHeight: 21 },
  readAll: { minHeight: 44, justifyContent: 'center', alignSelf: 'flex-start' },
  readAllText: { fontSize: typography.fontSize.md, color: colors.primaryLight, fontWeight: typography.fontWeight.semibold },
  allBox: { backgroundColor: colors.surface, borderRadius: borderRadius.lg, borderWidth: 1, borderColor: colors.border, padding: spacing.md },
  allText: { fontSize: typography.fontSize.md, color: colors.textSubtle, lineHeight: 21, marginBottom: spacing.sm },
  actions: { paddingHorizontal: spacing.lg, paddingBottom: spacing.lg, paddingTop: spacing.sm, gap: spacing.sm, borderTopWidth: 1, borderTopColor: colors.border },
  error: { color: colors.errorLight, fontSize: typography.fontSize.md, textAlign: 'center' },
  primaryBtn: { minHeight: 48, borderRadius: borderRadius.lg, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  primaryText: { color: colors.text, fontSize: typography.fontSize.lg, fontWeight: typography.fontWeight.semibold },
  secondaryBtn: { minHeight: 48, borderRadius: borderRadius.lg, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  secondaryText: { color: colors.textSubtle, fontSize: typography.fontSize.lg, fontWeight: typography.fontWeight.semibold },
  btnDisabled: { opacity: 0.6 },
});
