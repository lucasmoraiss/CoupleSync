// AC-010: Settings screen stub
import React from 'react';
import { View, Text, StyleSheet, SafeAreaView, TouchableOpacity, Alert, Switch } from 'react-native';
import { router } from 'expo-router';
import { logout } from '@/services/logout';
import { openNotificationListenerSettings } from '@/modules/integrations/notification-capture/NotificationListenerBridge';
import { colors } from '@/theme';
import { useConsentStore } from '@/modules/privacy/consentStore';
import { formatConsentDate } from '@/modules/privacy/consent';

export default function SettingsScreen() {
  const capture = useConsentStore((state) => state.record.capture);
  const consentLoaded = useConsentStore((state) => state.loaded);

  // Ligar sem aceite anterior abre a tela de consentimento; com aceite anterior religa na hora.
  // Desligar vale na hora: o uploader para de enviar assim que o estado muda.
  const handleCaptureToggle = (value: boolean) => {
    if (value && capture.acceptedAt === null) {
      router.push('/(main)/settings/capture-consent' as any);
      return;
    }
    void useConsentStore.getState().setCaptureEnabled(value);
  };

  const handleLogout = async () => {
    Alert.alert('Sair', 'Deseja realmente sair da conta? Você também será desconectado dos outros aparelhos em que usa esta conta.', [
      { text: 'Cancelar', style: 'cancel' },
      {
        text: 'Sair',
        style: 'destructive',
        onPress: async () => {
          await logout();
          router.replace('/login' as any);
        },
      },
    ]);
  };

  return (
    <SafeAreaView style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.title} accessibilityRole="header">Configurações</Text>
      </View>

      <View style={styles.section}>
        <View style={styles.menuItem}>
          <View style={styles.switchInfo}>
            <Text style={styles.menuText}>Captura de notificações bancárias</Text>
            <Text style={styles.menuDesc}>
              {capture.acceptedAt
                ? `Aceita em ${formatConsentDate(capture.acceptedAt)}. ${capture.enabled ? 'Ligada.' : 'Desligada: nada é enviado.'}`
                : 'Desligada. Ao ligar, você lê o que é lido e enviado e decide.'}
            </Text>
          </View>
          <Switch
            value={capture.enabled && capture.acceptedAt !== null}
            onValueChange={handleCaptureToggle}
            disabled={!consentLoaded}
            trackColor={{ false: colors.border, true: colors.primary }}
            thumbColor={capture.enabled ? colors.text : colors.textMuted}
            accessibilityRole="switch"
            accessibilityLabel="Captura de notificações bancárias"
            accessibilityState={{ checked: capture.enabled && capture.acceptedAt !== null, disabled: !consentLoaded }}
          />
        </View>
        <View style={styles.divider} />
        <TouchableOpacity style={styles.menuItem} onPress={openNotificationListenerSettings} accessibilityLabel="Abrir configurações de notificações do sistema" accessibilityRole="button">
          <Text style={styles.menuText}>Notificações do sistema</Text>
          <Text style={styles.menuArrow}>›</Text>
        </TouchableOpacity>
        <View style={styles.divider} />
        <TouchableOpacity
          style={styles.menuItem}
          onPress={() => router.push('/(main)/settings/alerts' as any)}
          accessibilityLabel="Abrir configurações de alertas"
          accessibilityRole="button"
        >
          <Text style={styles.menuText}>Alertas</Text>
          <Text style={styles.menuArrow}>›</Text>
        </TouchableOpacity>
        <View style={styles.divider} />
        <TouchableOpacity
          style={styles.menuItem}
          onPress={() => router.push('/(main)/settings/group' as any)}
          accessibilityLabel="Abrir o grupo: membros e código de convite"
          accessibilityRole="button"
        >
          <Text style={styles.menuText}>Grupo e código de convite</Text>
          <Text style={styles.menuArrow}>›</Text>
        </TouchableOpacity>
        <View style={styles.divider} />
        <TouchableOpacity
          style={styles.menuItem}
          onPress={() => router.push('/(main)/settings/change-password' as any)}
          accessibilityLabel="Alterar a senha da conta"
          accessibilityRole="button"
        >
          <Text style={styles.menuText}>Alterar senha</Text>
          <Text style={styles.menuArrow}>›</Text>
        </TouchableOpacity>
        <View style={styles.divider} />
        <TouchableOpacity
          style={styles.menuItem}
          onPress={() => router.push('/(main)/settings/privacy' as any)}
          accessibilityLabel="Abrir a privacidade: quais dados são coletados e como pedir a exclusão"
          accessibilityRole="button"
        >
          <Text style={styles.menuText}>Privacidade</Text>
          <Text style={styles.menuArrow}>›</Text>
        </TouchableOpacity>
        <View style={styles.divider} />
        <TouchableOpacity style={styles.menuItem} onPress={handleLogout} accessibilityLabel="Sair da conta" accessibilityRole="button">
          <Text style={[styles.menuText, { color: colors.error }]}>Sair da conta</Text>
          <Text style={[styles.menuArrow, { color: colors.error }]}>›</Text>
        </TouchableOpacity>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background, paddingHorizontal: 20, paddingTop: 24 },
  header: { marginBottom: 28 },
  title: { fontSize: 26, fontWeight: '700', color: colors.text },
  section: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, overflow: 'hidden' },
  menuItem: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingHorizontal: 20, paddingVertical: 18, minHeight: 56 },
  menuText: { fontSize: 16, color: colors.text, fontWeight: '500' },
  switchInfo: { flex: 1, marginRight: 16 },
  menuDesc: { fontSize: 13, color: colors.textMuted, marginTop: 3 },
  menuArrow: { fontSize: 22, color: colors.textDisabled },
  divider: { height: 1, backgroundColor: colors.border, marginHorizontal: 20 },
});
