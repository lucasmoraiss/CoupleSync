// Tela de bloqueio: a versão instalada é menor que a mínima aceita pelo servidor. Entra NO LUGAR das abas
// (app/(main)/_layout.tsx), então nenhuma tela de dados é montada. Não pode ser dispensada; sempre oferece o
// download e a saída da conta, e pergunta de novo ao servidor sozinha (ao voltar ao primeiro plano e a
// intervalos): se a versão mínima for corrigida lá, o aparelho destrava sem a pessoa fechar o app.
import React, { useEffect, useRef, useState } from 'react';
import { Alert, AppState, SafeAreaView, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { router } from 'expo-router';
import { Ionicons } from '@expo/vector-icons';
import { APP_UPDATE_TEXT, type AppUpdateState } from '@/modules/appUpdate/appUpdate';
import { startBlockRecheck } from '@/modules/appUpdate/blockRecheck';
import { openApkDownload } from '@/modules/appUpdate/openDownload';
import { logout } from '@/services/logout';
import { colors } from '@/theme';

export function AppUpdateRequiredScreen({ update }: { update: AppUpdateState & { refetch: () => void } }) {
  const [openFailure, setOpenFailure] = useState<string | null>(null);
  const [signingOut, setSigningOut] = useState(false);

  const recheckRef = useRef(update.refetch);
  recheckRef.current = update.refetch;
  useEffect(
    () =>
      startBlockRecheck({
        recheck: () => recheckRef.current(),
        onAppStateChange: (listener) => AppState.addEventListener('change', listener),
      }),
    [],
  );

  const signOut = async () => {
    if (signingOut) return;
    setSigningOut(true);
    await logout();
    router.replace('/login' as any);
  };

  // A mesma confirmação de Configurações: sair encerra a sessão também nos outros aparelhos.
  const confirmSignOut = () => {
    Alert.alert('Sair', 'Deseja realmente sair da conta? Você também será desconectado dos outros aparelhos em que usa esta conta.', [
      { text: 'Cancelar', style: 'cancel' },
      {
        text: 'Sair',
        style: 'destructive',
        onPress: () => void signOut(),
      },
    ]);
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
          onPress={confirmSignOut}
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
