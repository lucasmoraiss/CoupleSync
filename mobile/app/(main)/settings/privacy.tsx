// Privacidade: o que o app coleta, onde fica, com quem é compartilhado e como pedir a exclusão.
// Texto dentro do app (sem link externo). Mostra também as respostas de consentimento do usuário neste aparelho.
import React from 'react';
import { View, Text, StyleSheet, SafeAreaView, ScrollView, TouchableOpacity } from 'react-native';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { colors } from '@/theme';
import { TextSections } from '@/components/TextSections';
import { PRIVACY_SECTIONS, PRIVACY_TITLE } from '@/modules/privacy/privacyContent';
import { formatConsentDate } from '@/modules/privacy/consent';
import { useConsentStore } from '@/modules/privacy/consentStore';

export default function PrivacyScreen() {
  const record = useConsentStore((s) => s.record);
  const aiAccepted = record.aiChat.acceptedAt;

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <TouchableOpacity
          style={styles.backRow}
          onPress={() => router.back()}
          accessibilityRole="button"
          accessibilityLabel="Voltar para as configurações"
        >
          <Ionicons name="chevron-back" size={20} color={colors.primaryLight} />
          <Text style={styles.backText}>Configurações</Text>
        </TouchableOpacity>
        <Text style={styles.title} accessibilityRole="header">{PRIVACY_TITLE}</Text>
        <Text style={styles.subtitle}>Como o CoupleSync trata os seus dados.</Text>

        <TextSections sections={PRIVACY_SECTIONS} />

        <View style={styles.statusBox}>
          <Text style={styles.statusTitle} accessibilityRole="header">Suas respostas neste aparelho</Text>
          <Text style={styles.statusLine}>
            {record.capture.acceptedAt
              ? `Captura de notificações: aceita em ${formatConsentDate(record.capture.acceptedAt)} (${record.capture.enabled ? 'ligada' : 'desligada'}).`
              : 'Captura de notificações: não aceita.'}
          </Text>
          <Text style={styles.statusLine}>
            {aiAccepted
              ? `Chat IA (Google Gemini): aceito em ${formatConsentDate(aiAccepted)}.`
              : 'Chat IA (Google Gemini): não aceito.'}
          </Text>
        </View>
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
  statusBox: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 16 },
  statusTitle: { fontSize: 15, fontWeight: '700', color: colors.text, marginBottom: 8 },
  statusLine: { fontSize: 14, color: colors.textSubtle, marginBottom: 6, lineHeight: 20 },
});
