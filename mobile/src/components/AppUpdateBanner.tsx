// Aviso discreto "há uma versão nova do app" no Painel. Só aparece quando a versão nativa instalada é menor que
// a última publicada (regra em modules/appUpdate/appUpdate.ts); qualquer dúvida (módulo nativo ausente, servidor
// fora, sem internet) o esconde. Nunca bloqueia nada.
import React, { useEffect } from 'react';
import { StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { APP_UPDATE_TEXT, showDashboardNotice } from '@/modules/appUpdate/appUpdate';
import { useAppUpdateDismissal } from '@/modules/appUpdate/dismissalStore';
import { openApkDownload } from '@/modules/appUpdate/openDownload';
import { useAppUpdate } from '@/modules/appUpdate/useAppUpdate';
import { useOnRefocus } from '@/navigation/resetOnFocus';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { colors } from '@/theme';

export function AppUpdateBanner() {
  const update = useAppUpdate();
  const dismissal = useAppUpdateDismissal();
  const loadDismissal = dismissal.load;

  useEffect(() => {
    void loadDismissal();
  }, [loadDismissal]);

  // O Painel fica montado entre visitas: ao voltar para ele, pergunta de novo se a resposta já envelheceu.
  useOnRefocus(() => update.refetchIfStale());

  if (!showDashboardNotice(update, dismissal)) return null;

  return (
    <View style={styles.banner}>
      <Text style={styles.text}>{APP_UPDATE_TEXT.notice}</Text>
      <View style={styles.actions}>
        <TouchableOpacity
          style={styles.button}
          onPress={() => void dismissal.dismiss(update.latestVersion)}
          accessibilityRole="button"
          accessibilityLabel="Agora não: esconder o aviso de versão nova"
        >
          <Text style={styles.secondary}>{APP_UPDATE_TEXT.notNow}</Text>
        </TouchableOpacity>
        <TouchableOpacity
          style={styles.button}
          onPress={() => openApkDownload(update.downloadUrl, (message) => showToastGlobal(message, 'warning', 8000))}
          accessibilityRole="button"
          accessibilityLabel="Baixar a versão nova do app"
        >
          <Text style={styles.action}>{APP_UPDATE_TEXT.download}</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 12,
    marginHorizontal: 16,
    marginBottom: 12,
    paddingTop: 10,
    paddingHorizontal: 14,
  },
  text: { color: colors.textSubtle, fontSize: 13 },
  actions: { flexDirection: 'row', justifyContent: 'flex-end' },
  button: { minHeight: 44, minWidth: 44, justifyContent: 'center', paddingLeft: 20 },
  secondary: { color: colors.textMuted, fontSize: 13, fontWeight: '600' },
  action: { color: colors.primaryLight, fontSize: 13, fontWeight: '600' },
});
