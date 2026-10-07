// MOB-03: consentimento da captura de notificações bancárias. A captura só é ligada depois do aceite aqui.
import React, { useState } from 'react';
import { View, Text, StyleSheet, SafeAreaView, ScrollView, TouchableOpacity, ActivityIndicator } from 'react-native';
import { colors } from '@/theme';
import { goToParent, resetOnFocus } from '@/navigation/resetOnFocus';
import { useMyGroups } from '@/modules/couple/useMyGroups';
import { captureDestinationText } from '@/modules/couple/groups';
import { TextSections } from '@/components/TextSections';
import { CAPTURE_CONSENT_SECTIONS, CAPTURE_CONSENT_TITLE } from '@/modules/privacy/privacyContent';
import { useConsentStore } from '@/modules/privacy/consentStore';
import {
  checkNotificationListenerPermission,
  isNotificationBridgeAvailable,
  openNotificationListenerSettings,
} from '@/modules/integrations/notification-capture/NotificationListenerBridge';

// Aberta de Configurações, do aviso da tela de Transações e do pedido inicial: a resposta sempre leva a
// Configurações, onde o resultado (captura ligada/desligada e o que falta) está à vista.
function leave() {
  goToParent('settings/capture-consent');
}

function CaptureConsentScreen() {
  const { data: myGroups } = useMyGroups();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const handleAccept = async () => {
    if (busy) return;
    setBusy(true);
    setFailed(false);
    try {
      if (!(await useConsentStore.getState().acceptCapture())) {
        setFailed(true); // nada foi registrado: a tela fica e avisa
        return;
      }
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
    setFailed(false);
    if (!(await useConsentStore.getState().declineCapture())) {
      setFailed(true);
      return;
    }
    leave();
  };

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView contentContainerStyle={styles.content}>
        <Text style={styles.title} accessibilityRole="header">{CAPTURE_CONSENT_TITLE}</Text>
        <Text style={styles.subtitle}>Leia antes de ligar. Nada é enviado nem guardado sem o seu aceite.</Text>
        <Text style={styles.subtitle}>{captureDestinationText(myGroups)}</Text>
        <TextSections sections={CAPTURE_CONSENT_SECTIONS} />
      </ScrollView>

      <View style={styles.actions}>
        {failed && (
          <Text style={styles.errorText} accessibilityRole="alert" accessibilityLiveRegion="assertive">
            Não foi possível registrar a sua resposta. Tente novamente.
          </Text>
        )}
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

// Cada visita começa limpa: o aviso "não foi possível registrar" não fica de uma tentativa anterior.
export default resetOnFocus(CaptureConsentScreen);

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 24 },
  title: { fontSize: 24, fontWeight: '700', color: colors.text },
  subtitle: { fontSize: 14, color: colors.textMuted, marginTop: 4, marginBottom: 24 },
  actions: { paddingHorizontal: 20, paddingBottom: 20, paddingTop: 8, gap: 10, borderTopWidth: 1, borderTopColor: colors.border },
  errorText: { color: colors.errorLight, fontSize: 14, textAlign: 'center' },
  primaryBtn: { minHeight: 48, borderRadius: 12, backgroundColor: colors.primary, alignItems: 'center', justifyContent: 'center' },
  primaryText: { color: colors.text, fontSize: 16, fontWeight: '700' },
  secondaryBtn: { minHeight: 48, borderRadius: 12, borderWidth: 1, borderColor: colors.border, alignItems: 'center', justifyContent: 'center' },
  secondaryText: { color: colors.textSubtle, fontSize: 16, fontWeight: '600' },
});
