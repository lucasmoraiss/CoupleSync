// Assistente (aba oculta, aberta pelo Painel): conversa com a IA sobre as finanças do grupo. O histórico vive só
// enquanto o app está aberto. Quem decide se há conversa é o servidor (GET /ai/status): IA indisponível, grupo
// sem ativação (explicação e "Ativar") ou pronta.
import React, { useState, useRef, useEffect, useCallback } from 'react';
import {
  View,
  Text,
  StyleSheet,
  SafeAreaView,
  FlatList,
  TouchableOpacity,
  TextInput,
  KeyboardAvoidingView,
  Platform,
  ActivityIndicator,
  ScrollView,
} from 'react-native';
import { router } from 'expo-router';
import * as Haptics from 'expo-haptics';
import { Ionicons } from '@expo/vector-icons';
import { useChat } from '../hooks/useChat';
import type { Message } from '../hooks/useChat';
import { colors, spacing, typography, borderRadius } from '@/theme';
import { ErrorState } from '@/components/ErrorState';
import { goToParent } from '@/navigation/resetOnFocus';
import { assistantGate } from '@/modules/ai/aiStatus';
import { useAiStatus } from '@/modules/ai/useAiStatus';
import { AI_ANALYSIS_SUMMARY } from '@/modules/privacy/privacyContent';

// Sugestões da tela vazia: um toque envia a pergunta.
const SUGGESTED_QUESTIONS: readonly string[] = [
  'Quanto gastamos este mês?',
  'Em que categoria gastamos mais?',
  'Como estão as nossas metas?',
];

// A resposta leva de 1 a 15 segundos (às vezes mais): depois deste tempo o aviso de espera muda, para a pessoa
// saber que o app não travou.
const SLOW_ANSWER_AFTER_MS = 6000;

function AssistantHeader() {
  return (
    <View style={styles.header}>
      <TouchableOpacity
        style={styles.backBtn}
        onPress={() => goToParent('chat/index')}
        accessibilityRole="button"
        accessibilityLabel="Voltar para o Painel"
      >
        <Ionicons name="chevron-back" size={22} color={colors.primaryLight} />
      </TouchableOpacity>
      <Ionicons name="sparkles" size={20} color={colors.primary} />
      <Text style={styles.headerTitle} accessibilityRole="header">Assistente</Text>
    </View>
  );
}

/** Tela do Assistente para quem não pode conversar agora: um texto e, quando há o que fazer, um botão. */
function AssistantNotice({
  title,
  text,
  actionLabel,
  actionAccessibilityLabel,
  onAction,
}: {
  title: string;
  text: string;
  actionLabel?: string;
  actionAccessibilityLabel?: string;
  onAction?: () => void;
}) {
  return (
    <SafeAreaView style={styles.safeArea}>
      <AssistantHeader />
      <ScrollView contentContainerStyle={styles.noticeContent}>
        <Text style={styles.noticeTitle} accessibilityRole="header">{title}</Text>
        <Text style={styles.noticeText}>{text}</Text>
        {actionLabel && onAction ? (
          <TouchableOpacity
            style={styles.noticeBtn}
            onPress={onAction}
            accessibilityRole="button"
            accessibilityLabel={actionAccessibilityLabel ?? actionLabel}
          >
            <Text style={styles.noticeBtnText}>{actionLabel}</Text>
          </TouchableOpacity>
        ) : null}
      </ScrollView>
    </SafeAreaView>
  );
}

export default function ChatScreen() {
  const { status, loadFailed, refresh } = useAiStatus();
  const gate = assistantGate(status, loadFailed);

  if (gate === 'loading') {
    return (
      <SafeAreaView style={styles.safeArea}>
        <AssistantHeader />
        <View style={styles.centered}>
          <ActivityIndicator color={colors.primary} accessibilityLabel="Carregando o Assistente" />
        </View>
      </SafeAreaView>
    );
  }
  if (gate === 'error') {
    return (
      <SafeAreaView style={styles.safeArea}>
        <AssistantHeader />
        <ErrorState message="Não foi possível abrir o Assistente. Verifique a internet e tente novamente." onRetry={() => void refresh()} />
      </SafeAreaView>
    );
  }
  if (gate === 'unavailable') {
    return (
      <AssistantNotice
        title="Assistente indisponível"
        text="A análise com IA não está disponível no momento. O restante do app funciona normalmente, com os números atualizados."
      />
    );
  }
  if (gate === 'needs-activation') {
    return (
      <AssistantNotice
        title="A análise com IA está desligada para este grupo"
        text={`O Assistente responde às suas perguntas sobre gastos, orçamento e metas do grupo. ${AI_ANALYSIS_SUMMARY} Uma pessoa ativa e vale para o grupo inteiro; qualquer um desliga quando quiser.`}
        actionLabel="Ativar"
        actionAccessibilityLabel="Ativar a análise com IA: ver o que é enviado e decidir"
        onAction={() => router.push('/(main)/ai/welcome' as any)}
      />
    );
  }
  return <ChatConversation />;
}

/** Aviso de espera da resposta: muda depois de alguns segundos, para ficar claro que a IA ainda está respondendo. */
function WaitingBubble() {
  const [slow, setSlow] = useState(false);
  useEffect(() => {
    const timer = setTimeout(() => setSlow(true), SLOW_ANSWER_AFTER_MS);
    return () => clearTimeout(timer);
  }, []);
  const text = slow ? 'Ainda pensando… a resposta pode levar até meio minuto.' : 'Pensando na resposta…';
  return (
    <View style={styles.loadingBubble} accessible accessibilityLabel={text} accessibilityLiveRegion="polite">
      <ActivityIndicator size="small" color={colors.textMuted} />
      <Text style={styles.loadingText}>{text}</Text>
    </View>
  );
}

function ChatConversation() {
  const { messages, isLoading, error, sendMessage, clearError } = useChat();
  const [inputText, setInputText] = useState('');
  const flatListRef = useRef<FlatList<Message>>(null);

  // Auto-scroll to bottom on new messages or loading state change
  useEffect(() => {
    if (messages.length > 0 || isLoading) {
      setTimeout(() => flatListRef.current?.scrollToEnd({ animated: true }), 100);
    }
  }, [messages.length, isLoading]);

  const handleSend = () => {
    const text = inputText.trim();
    if (!text || isLoading) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setInputText('');
    sendMessage(text);
  };

  const renderBubble = useCallback(({ item }: { item: Message }) => {
    const isUser = item.role === 'user';
    return (
      <View style={[styles.bubbleRow, isUser ? styles.bubbleRowUser : styles.bubbleRowAi]}>
        {!isUser && (
          <View style={styles.aiAvatar} accessible={false} importantForAccessibility="no-hide-descendants">
            <Ionicons name="sparkles" size={14} color={colors.primary} />
          </View>
        )}
        <View
          style={[
            styles.bubble,
            isUser ? styles.bubbleUser : styles.bubbleAi,
          ]}
          accessible
          accessibilityLabel={`${isUser ? 'Você' : 'Assistente'}: ${item.content}`}
        >
          <Text style={[styles.bubbleText, isUser ? styles.bubbleTextUser : styles.bubbleTextAi]}>
            {item.content}
          </Text>
        </View>
      </View>
    );
  }, []);

  return (
    <SafeAreaView style={styles.safeArea}>
      <AssistantHeader />

      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
        keyboardVerticalOffset={Platform.OS === 'ios' ? 0 : 24}
      >
        {messages.length === 0 && !isLoading ? (
          <ScrollView contentContainerStyle={styles.emptyContent} keyboardShouldPersistTaps="handled">
            <Text style={styles.emptyTitle} accessibilityRole="header">Olá! Sou o Assistente do grupo</Text>
            <Text style={styles.emptyText}>Pergunte sobre o orçamento, os gastos ou as metas. Por exemplo:</Text>
            {SUGGESTED_QUESTIONS.map((question) => (
              <TouchableOpacity
                key={question}
                style={styles.suggestion}
                onPress={() => sendMessage(question)}
                accessibilityRole="button"
                accessibilityLabel={`Perguntar: ${question}`}
              >
                <Text style={styles.suggestionText}>{question}</Text>
              </TouchableOpacity>
            ))}
            <Text style={styles.emptyHint}>A resposta pode levar alguns segundos.</Text>
          </ScrollView>
        ) : (
          <FlatList
            ref={flatListRef}
            data={messages}
            keyExtractor={(item) => item.id}
            renderItem={renderBubble}
            contentContainerStyle={styles.listContent}
            showsVerticalScrollIndicator={false}
            ListFooterComponent={isLoading ? <WaitingBubble /> : null}
          />
        )}

        {error != null && (
          <TouchableOpacity accessibilityRole="button" accessibilityLabel={`Fechar o aviso: ${error}`} style={styles.errorBanner} onPress={clearError} activeOpacity={0.8}>
            <Ionicons name="alert-circle-outline" size={16} color={colors.errorLight} />
            <Text style={styles.errorText}>{error}</Text>
            <Ionicons name="close" size={14} color={colors.errorLight} />
          </TouchableOpacity>
        )}

        <View style={styles.inputRow}>
          <TextInput accessibilityLabel="Pergunta para o Assistente"
            style={styles.textInput}
            value={inputText}
            onChangeText={setInputText}
            placeholder="Digite sua pergunta..."
            placeholderTextColor={colors.placeholder}
            multiline
            maxLength={2000}
            returnKeyType="send"
            onSubmitEditing={handleSend}
            blurOnSubmit={false}
          />
          <TouchableOpacity
            style={[styles.sendBtn, (!inputText.trim() || isLoading) && styles.sendBtnDisabled]}
            onPress={handleSend}
            disabled={!inputText.trim() || isLoading}
            accessibilityRole="button"
            accessibilityLabel="Enviar mensagem"
            activeOpacity={0.7}
          >
            <Ionicons
              name="send"
              size={18}
              color={(!inputText.trim() || isLoading) ? colors.textDisabled : colors.text}
            />
          </TouchableOpacity>
        </View>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: colors.background,
  },
  flex: {
    flex: 1,
  },
  centered: { flex: 1, justifyContent: 'center', alignItems: 'center' },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.sm,
    paddingVertical: spacing.sm,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
  },
  backBtn: { minWidth: 44, minHeight: 44, alignItems: 'center', justifyContent: 'center' },
  headerTitle: {
    fontSize: typography.fontSize.lg,
    fontWeight: typography.fontWeight.semibold,
    color: colors.text,
  },
  noticeContent: { paddingHorizontal: spacing.lg, paddingTop: spacing.lg, paddingBottom: spacing.lg },
  noticeTitle: { fontSize: typography.fontSize.xxl, fontWeight: typography.fontWeight.semibold, color: colors.text, marginBottom: spacing.md },
  noticeText: { fontSize: typography.fontSize.md, color: colors.textSubtle, lineHeight: 21, marginBottom: spacing.lg },
  noticeBtn: { minHeight: 48, borderRadius: borderRadius.lg, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  noticeBtnText: { color: colors.text, fontSize: typography.fontSize.lg, fontWeight: typography.fontWeight.semibold },
  emptyContent: { flexGrow: 1, justifyContent: 'center', paddingHorizontal: spacing.lg, paddingVertical: spacing.lg },
  emptyTitle: { fontSize: typography.fontSize.xl, fontWeight: typography.fontWeight.semibold, color: colors.text, marginBottom: spacing.sm, textAlign: 'center' },
  emptyText: { fontSize: typography.fontSize.md, color: colors.textSubtle, marginBottom: spacing.md, textAlign: 'center' },
  suggestion: {
    minHeight: 48,
    justifyContent: 'center',
    backgroundColor: colors.surface,
    borderRadius: borderRadius.lg,
    borderWidth: 1,
    borderColor: colors.border,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    marginBottom: spacing.sm,
  },
  suggestionText: { fontSize: typography.fontSize.md, color: colors.primaryLight, fontWeight: typography.fontWeight.semibold },
  emptyHint: { fontSize: typography.fontSize.sm, color: colors.textMuted, marginTop: spacing.sm, textAlign: 'center' },
  listContent: {
    flexGrow: 1,
    paddingHorizontal: spacing.md,
    paddingTop: spacing.md,
    paddingBottom: spacing.sm,
  },
  bubbleRow: {
    flexDirection: 'row',
    marginBottom: spacing.sm,
    alignItems: 'flex-end',
  },
  bubbleRowUser: {
    justifyContent: 'flex-end',
  },
  bubbleRowAi: {
    justifyContent: 'flex-start',
  },
  aiAvatar: {
    width: 28,
    height: 28,
    borderRadius: borderRadius.full,
    backgroundColor: colors.surface,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: spacing.xs,
    marginBottom: spacing.xs,
  },
  bubble: {
    maxWidth: '78%',
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: borderRadius.lg,
  },
  bubbleUser: {
    backgroundColor: colors.primary,
    borderBottomRightRadius: borderRadius.sm,
  },
  bubbleAi: {
    backgroundColor: colors.surface,
    borderBottomLeftRadius: borderRadius.sm,
  },
  bubbleText: {
    fontSize: typography.fontSize.md,
    lineHeight: 20,
  },
  bubbleTextUser: {
    color: colors.text,
  },
  bubbleTextAi: {
    color: colors.textSubtle,
  },
  loadingBubble: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    backgroundColor: colors.surface,
    borderRadius: borderRadius.lg,
    borderBottomLeftRadius: borderRadius.sm,
    alignSelf: 'flex-start',
    marginLeft: 36,
    marginBottom: spacing.sm,
  },
  loadingText: {
    color: colors.textMuted,
    fontSize: typography.fontSize.sm,
  },
  errorBanner: {
    minHeight: 44,
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.xs,
    backgroundColor: colors.surface,
    borderLeftWidth: 3,
    borderLeftColor: colors.error,
    marginHorizontal: spacing.md,
    marginBottom: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderRadius: borderRadius.md,
  },
  errorText: {
    flex: 1,
    color: colors.errorLight,
    fontSize: typography.fontSize.sm,
  },
  inputRow: {
    flexDirection: 'row',
    alignItems: 'flex-end',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    borderTopWidth: 1,
    borderTopColor: colors.border,
    backgroundColor: colors.background,
  },
  textInput: {
    flex: 1,
    minHeight: 44,
    maxHeight: 120,
    backgroundColor: colors.surface,
    borderRadius: borderRadius.lg,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.sm,
    fontSize: typography.fontSize.md,
    color: colors.text,
    borderWidth: 1,
    borderColor: colors.border,
  },
  sendBtn: {
    width: 48,
    height: 48,
    borderRadius: borderRadius.full,
    backgroundColor: colors.primary,
    justifyContent: 'center',
    alignItems: 'center',
  },
  sendBtnDisabled: {
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
  },
});
