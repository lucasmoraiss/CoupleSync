// Tela de bloqueio: a versão instalada é menor que a mínima aceita pelo servidor. Entra NO LUGAR das abas
// (app/(main)/_layout.tsx), então nenhuma tela de dados é montada. Não pode ser dispensada; sempre oferece o
// download e a saída da conta, para ninguém ficar preso se a versão mínima for configurada errada.
import React, { useState } from 'react';
import { SafeAreaView, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { APP_UPDATE_TEXT, type AppUpdateState } from '@/modules/appUpdate/appUpdate';
import { openApkDownload } from '@/modules/appUpdate/openDownload';
import { logout } from '@/services/logout';
import { colors } from '@/theme';

export function AppUpdateRequiredScreen({ update }: { update: AppUpdateState }) {
  const [openFailure, setOpenFailure] = useState<string | null>(null);
  const [signingOut, setSigningOut] = useState(false);

  const handleSignOut = async () => {
    if (signingOut) return;
    setSigningOut(true);
    await logout();
    router.replace('/login' as any);
  };

  return (
    <SafeAreaView style={styles.container}>
      <View style={styles.content}>
        <Ionicons name="cloud-download-outline" size={56} color={colors.primaryLight} />
        <Text style={styles.message} accessibilityRole="header">{APP_UPDATE_TEXT.required}</Text>
        {update.installedVersion ? (
          <Text style={styles.detail}>
            {update.latestVersion
              ? `Versão instalada: ${update.installedVersion}. Versão nova: ${update.latestVersion}.`
              : `Versão instalada: ${update.installedVersion}.`}
          </Text>
        ) : null}
        <TouchableOpacity
          style={styles.primaryBtn}
          onPress={() => {
            setOpenFailure(null);
            return openApkDownload(update.downloadUrl, setOpenFailure);
          }}
          accessibilityRole="button"
          accessibilityLabel="Baixar a versão nova do app"
          activeOpacity={0.8}
        >
          <Text style={styles.primaryText}>{APP_UPDATE_TEXT.download}</Text>
        </TouchableOpacity>
        {openFailure ? <Text style={styles.failure} accessibilityRole="alert">{openFailure}</Text> : null}
        <TouchableOpacity
          style={styles.secondaryBtn}
          onPress={handleSignOut}
          disabled={signingOut}
          accessibilityRole="button"
          accessibilityLabel="Sair da conta"
          accessibilityState={{ disabled: signingOut }}
        >
          <Text style={styles.secondaryText}>{APP_UPDATE_TEXT.signOut}</Text>
        </TouchableOpacity>
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { flex: 1, justifyContent: 'center', alignItems: 'center', paddingHorizontal: 32 },
  message: { color: colors.text, fontSize: 18, fontWeight: '600', textAlign: 'center', marginTop: 20 },
  detail: { color: colors.textMuted, fontSize: 13, textAlign: 'center', marginTop: 8 },
  primaryBtn: {
    minHeight: 48,
    alignSelf: 'stretch',
    justifyContent: 'center',
    alignItems: 'center',
    backgroundColor: colors.primary,
    borderRadius: 12,
    marginTop: 28,
  },
  primaryText: { color: colors.white, fontSize: 16, fontWeight: '600' },
  failure: { color: colors.errorLight, fontSize: 13, textAlign: 'center', marginTop: 12 },
  secondaryBtn: { minHeight: 48, justifyContent: 'center', alignItems: 'center', paddingHorizontal: 16, marginTop: 12 },
  secondaryText: { color: colors.error, fontSize: 15, fontWeight: '500' },
});
