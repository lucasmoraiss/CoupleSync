// MOB-03: consentimento da captura de notificações bancárias. A captura só é ligada depois do aceite aqui.
import React, { useState } from 'react';
import { View, Text, StyleSheet, SafeAreaView, ScrollView, TouchableOpacity, ActivityIndicator } from 'react-native';
import { router } from 'expo-router';
import { colors } from '@/theme';
import { TextSections } from '@/components/TextSections';
import { CAPTURE_CONSENT_SECTIONS, CAPTURE_CONSENT_TITLE } from '@/modules/privacy/privacyContent';
import { useConsentStore } from '@/modules/privacy/consentStore';
import {
  checkNotificationListenerPermission,
  isNotificationBridgeAvailable,
  openNotificationListenerSettings,
} from '@/modules/integrations/notification-capture/NotificationListenerBridge';

function leave() {
  if (router.canGoBack()) router.back();
  else router.replace('/(main)/settings' as any);
}

export default function CaptureConsentScreen() {
  const [busy, setBusy] = useState(false);

  const handleAccept = async () => {
    if (busy) return;
    setBusy(true);
    try {
      await useConsentStore.getState().acceptCapture();
      // A captura precisa também da permissão do Android; se ainda não existe, leva o usuário até ela.
      if (isNotificationBridgeAvailable() && !(await checkNotificationListenerPermission())) {
        openNotificationListenerSettings();
      }
    } finally {
      setBusy(false);
    }
    leave();
  };

  const handleDecline = async () => {
    if (busy) return;
    await useConsentStore.getState().declineCapture();
    leave();
  };

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <Text style={styles.title} accessibilityRole="header">{CAPTURE_CONSENT_TITLE}</Text>
        <Text style={styles.subtitle}>Leia antes de ligar. Nada é lido ou enviado sem o seu aceite.</Text>
        <TextSections sections={CAPTURE_CONSENT_SECTIONS} />
      </ScrollView>

      <View style={styles.actions}>
        <TouchableOpacity
          style={styles.primaryBtn}
          onPress={handleAccept}
          disabled={busy}
          accessibilityRole="button"
          accessibilityLabel="Aceitar e ligar a captura de notificações"
          accessibilityState={{ disabled: busy, busy }}
        >
          {busy ? <ActivityIndicator color={colors.text} /> : <Text style={styles.primaryText}>Aceitar e ligar</Text>}
        </TouchableOpacity>
        <TouchableOpacity
          style={styles.secondaryBtn}
          onPress={handleDecline}
          disabled={busy}
          accessibilityRole="button"
          accessibilityLabel="Agora não, manter a captura desligada"
        >
          <Text style={styles.secondaryText}>Agora não</Text>
        </TouchableOpacity>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 24 },
  title: { fontSize: 24, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, marginTop: 4, marginBottom: 24 },
  actions: { paddingHorizontal: 20, paddingBottom: 20, paddingTop: 8, gap: 10, borderTopWidth: 1, borderTopColor: colors.border },
  primaryBtn: { minHeight: 48, borderRadius: 12, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  primaryText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  secondaryBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  secondaryText: { color: colors.textSubtle, fontSize: 16, fontWeight: '600' },
});
