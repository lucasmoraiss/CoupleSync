// AC-142, AC-143, AC-144, AC-145, AC-147, AC-148: AI Chat screen — ephemeral message history
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
import * as Haptics from 'expo-haptics';
import { Ionicons } from '@expo/vector-icons';
import { useChat } from '../hooks/useChat';
import type { Message } from '../hooks/useChat';
import { colors, spacing, typography, borderRadius } from '@/theme';
import { EmptyState } from '@/components/EmptyState';
import { TextSections } from '@/components/TextSections';
import { AI_CHAT_SECTIONS, AI_CHAT_TITLE } from '@/modules/privacy/privacyContent';
import { useConsentStore } from '@/modules/privacy/consentStore';

/** SEG-10: primeira abertura do chat: aviso de envio ao Google Gemini, com aceitar / não usar. */
function AiChatDisclosure({ declined }: { declined: boolean }) {
  const [failed, setFailed] = useState(false);
  // Só considera feito se a resposta foi registrada; senão fica na tela e avisa.
  const answer = async (accept: boolean) => {
    setFailed(false);
    const store = useConsentStore.getState();
    const saved = accept ? await store.acceptAiChat() : await store.declineAiChat();
    if (!saved) setFailed(true);
  };
  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView contentContainerStyle={styles.disclosureContent}>
        <Text style={styles.disclosureTitle} accessibilityRole="header">{AI_CHAT_TITLE}</Text>
        {declined && (
          <Text style={styles.disclosureNote}>O Chat IA está desativado. Você pode aceitar quando quiser.</Text>
        )}
        <TextSections sections={AI_CHAT_SECTIONS} />
      </ScrollView>
      <View style={styles.disclosureActions}>
        {failed && (
          <Text style={styles.disclosureError} accessibilityRole="alert" accessibilityLiveRegion="assertive">
            Não foi possível registrar a sua resposta. Tente novamente.
          </Text>
        )}
        <TouchableOpacity
          style={styles.acceptBtn}
          onPress={() => void answer(true)}
          accessibilityRole="button"
          accessibilityLabel="Aceitar e usar o Chat IA. Meus dados financeiros serão enviados ao Google Gemini."
        >
          <Text style={styles.acceptText}>Aceitar e usar o Chat IA</Text>
        </TouchableOpacity>
        {!declined && (
          <TouchableOpacity
            style={styles.declineBtn}
            onPress={() => void answer(false)}
            accessibilityRole="button"
            accessibilityLabel="Não usar o Chat IA"
          >
            <Text style={styles.declineText}>Não usar</Text>
          </TouchableOpacity>
        )}
      </View>
    </SafeAreaView>
  );
}

export default function ChatScreen() {
  const consentLoaded = useConsentStore((s) => s.loaded);
  const aiRecord = useConsentStore((s) => s.record.aiChat);
  const accepted = consentLoaded && aiRecord.acceptedAt !== null;

  if (!consentLoaded) {
    return (
      <SafeAreaView style={[styles.safeArea, { justifyContent: 'center', alignItems: 'center' }]}>
        <ActivityIndicator color={colors.primary} />
      </SafeAreaView>
    );
  }
  if (!accepted) return <AiChatDisclosure declined={aiRecord.declinedAt !== null} />;
  return <ChatConversation />;
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
      <View style={styles.header}>
        <Ionicons name="sparkles" size={20} color={colors.primary} />
        <Text style={styles.headerTitle} accessibilityRole="header">Chat IA</Text>
      </View>

      <KeyboardAvoidingView
        style={styles.flex}
        behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
        keyboardVerticalOffset={Platform.OS === 'ios' ? 0 : 24}
      >
        {messages.length === 0 && !isLoading ? (
          <EmptyState
            title="Olá! Sou seu assistente financeiro"
            subtitle="Pergunte sobre seu orçamento, gastos ou metas. Ex: 'Qual meu saldo em alimentação este mês?'"
          />
        ) : (
          <FlatList
            ref={flatListRef}
            data={messages}
            keyExtractor={(item) => item.id}
            renderItem={renderBubble}
            contentContainerStyle={styles.listContent}
            showsVerticalScrollIndicator={false}
            ListFooterComponent={
              isLoading ? (
                <View style={styles.loadingBubble} accessible accessibilityLabel="Analisando sua pergunta" accessibilityLiveRegion="polite">
                  <ActivityIndicator size="small" color={colors.textMuted} />
                  <Text style={styles.loadingText}>Analisando...</Text>
                </View>
              ) : null
            }
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
          <TextInput accessibilityLabel="Pergunta para o Chat IA"
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
  disclosureContent: { paddingHorizontal: spacing.lg, paddingTop: spacing.lg, paddingBottom: spacing.lg },
  disclosureTitle: { fontSize: typography.fontSize.xxl, fontWeight: typography.fontWeight.semibold, color: colors.text, marginBottom: spacing.md },
  disclosureError: { color: colors.errorLight, fontSize: typography.fontSize.md, textAlign: 'center' },
  disclosureNote: { fontSize: typography.fontSize.md, color: colors.warning, marginBottom: spacing.md },
  disclosureActions: { paddingHorizontal: spacing.lg, paddingBottom: spacing.lg, paddingTop: spacing.sm, gap: spacing.sm, borderTopWidth: 1, borderTopColor: colors.border },
  acceptBtn: { minHeight: 48, borderRadius: borderRadius.lg, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  acceptText: { color: colors.text, fontSize: typography.fontSize.lg, fontWeight: typography.fontWeight.semibold },
  declineBtn: { minHeight: 48, borderRadius: borderRadius.lg, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  declineText: { color: colors.textSubtle, fontSize: typography.fontSize.lg, fontWeight: typography.fontWeight.semibold },
  safeArea: {
    flex: 1,
    backgroundColor: colors.background,
  },
  flex: {
    flex: 1,
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.sm,
    paddingHorizontal: spacing.md,
    paddingVertical: spacing.md,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
  },
  headerTitle: {
    fontSize: typography.fontSize.lg,
    fontWeight: typography.fontWeight.semibold,
    color: colors.text,
  },
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
