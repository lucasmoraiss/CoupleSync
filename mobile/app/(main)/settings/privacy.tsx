// Privacidade: o que o app coleta, onde fica, com quem é compartilhado e como pedir a exclusão.
// Texto dentro do app (sem link externo). Mostra também as respostas de consentimento do usuário neste aparelho.
// A análise com IA tem a seção própria, em destaque e separada do texto geral: o aceite dela é do grupo e fica no
// servidor (Configurações > Inteligência artificial).
import React from 'react';
import { View, Text, StyleSheet, SafeAreaView, ScrollView, TouchableOpacity } from 'react-native';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { colors } from '@/theme';
import { TextSections } from '@/components/TextSections';
import { aiAnalysisSections, PRIVACY_TITLE, consentStatusLines, privacySections } from '@/modules/privacy/privacyContent';
import { goToParent } from '@/navigation/resetOnFocus';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { aiDestinations } from '@/modules/ai/aiStatus';
import { useAiStatus } from '@/modules/ai/useAiStatus';

export default function PrivacyScreen() {
  const record = useConsentStore((s) => s.record);
  // Os destinos da IA são os que o servidor tem ligados; sem resposta dele, o texto cita todos os possíveis.
  const { status } = useAiStatus();

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <TouchableOpacity
          style={styles.backRow}
          onPress={() => goToParent('settings/privacy')}
          accessibilityRole="button"
          accessibilityLabel="Voltar para as configurações"
        >
          <Ionicons name="chevron-back" size={20} color={colors.primaryLight} />
          <Text style={styles.backText}>Configurações</Text>
        </TouchableOpacity>
        <Text style={styles.title} accessibilityRole="header">{PRIVACY_TITLE}</Text>
        <Text style={styles.subtitle}>Como o CoupleSync trata os seus dados.</Text>

        <View style={styles.aiBox}>
          <TextSections sections={aiAnalysisSections(aiDestinations(status))} />
          <TouchableOpacity
            style={styles.aiLink}
            onPress={() => router.push('/(main)/settings/ai' as any)}
            accessibilityRole="button"
            accessibilityLabel="Abrir as configurações de inteligência artificial: ativar, desligar e ver o consumo"
          >
            <Text style={styles.aiLinkText}>Ativar, desligar e ver o consumo</Text>
            <Ionicons name="chevron-forward" size={18} color={colors.primaryLight} />
          </TouchableOpacity>
        </View>

        <TextSections sections={privacySections()} />

        <View style={styles.statusBox}>
          <Text style={styles.statusTitle} accessibilityRole="header">Suas respostas neste aparelho</Text>
          {consentStatusLines(record).map((line) => (
            <Text key={line} style={styles.statusLine}>{line}</Text>
          ))}
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
  // Em destaque: borda na cor principal, separada do texto geral.
  aiBox: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 2, borderColor: colors.primary, padding: 16, marginBottom: 24 },
  aiLink: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', minHeight: 44 },
  aiLinkText: { fontSize: 15, color: colors.primaryLight, fontWeight: '600' },
  statusBox: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 16 },
  statusTitle: { fontSize: 15, fontWeight: '700', color: colors.text, marginBottom: 8 },
  statusLine: { fontSize: 14, color: colors.textSubtle, marginBottom: 6, lineHeight: 20 },
});
