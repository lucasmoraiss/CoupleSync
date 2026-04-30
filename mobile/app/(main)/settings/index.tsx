// AC-010: Settings screen stub
import React from 'react';
import { View, Text, StyleSheet, SafeAreaView, TouchableOpacity, Alert } from 'react-native';
import { router } from 'expo-router';
import { useSessionStore } from '@/state/sessionStore';
import { openNotificationListenerSettings } from '@/modules/integrations/notification-capture/NotificationListenerBridge';
import { colors } from '@/theme';

export default function SettingsScreen() {
  const handleLogout = async () => {
    Alert.alert('Sair', 'Deseja realmente sair da conta?', [
      { text: 'Cancelar', style: 'cancel' },
      {
        text: 'Sair',
        style: 'destructive',
        onPress: async () => {
          await useSessionStore.getState().clearSession();
          router.replace('/login' as any);
        },
      },
    ]);
  };

  return (
    <SafeAreaView style={styles.container}>
      <View style={styles.header}>
        <Text style={styles.title}>Configurações</Text>
      </View>

      <View style={styles.section}>
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
        <TouchableOpacity style={[styles.menuItem, styles.menuItemDisabled]} accessibilityLabel="Código do casal" accessibilityRole="button">
          <Text style={[styles.menuText, styles.menuTextMuted]}>Código do casal</Text>
          {/* V1: couple code shown on couple-setup screen; deep-link not yet implemented */}
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
  menuItem: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', paddingHorizontal: 20, paddingVertical: 18 },
  menuItemDisabled: { opacity: 0.45 },
  menuText: { fontSize: 16, color: colors.text, fontWeight: '500' },
  menuTextMuted: { color: colors.textDisabled },
  menuArrow: { fontSize: 22, color: colors.textDisabled },
  divider: { height: 1, backgroundColor: colors.border, marginHorizontal: 20 },
});
