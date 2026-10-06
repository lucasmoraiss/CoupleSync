// Aviso discreto "confirme seu e-mail". Só aparece quando o servidor diz que o e-mail não foi confirmado;
// qualquer erro (servidor antigo sem a rota, sem conexão) o esconde. Nunca bloqueia nada.
import React, { useState } from 'react';
import { StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { router } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { authApiClient } from '@/services/apiClient';
import { useSessionStore } from '@/state/sessionStore';
import { colors } from '@/theme';

export const ME_QUERY_KEY = ['auth-me'] as const;

export function EmailVerificationBanner() {
  const accessToken = useSessionStore((s) => s.accessToken);
  const [dismissed, setDismissed] = useState(false);

  const { data } = useQuery({
    queryKey: ME_QUERY_KEY,
    queryFn: async () => (await authApiClient.getMe()).data,
    enabled: !!accessToken,
    retry: false,
    staleTime: 5 * 60_000,
  });

  if (dismissed || !data || data.emailVerified !== false) return null;

  return (
    <View style={styles.banner}>
      <TouchableOpacity
        style={styles.main}
        onPress={() => router.push('/(main)/settings/verify-email' as any)}
        accessibilityRole="button"
        accessibilityLabel="Confirmar e-mail"
      >
        <Text style={styles.text}>
          Confirme seu e-mail. <Text style={styles.action}>Digitar código</Text>
        </Text>
      </TouchableOpacity>
      <TouchableOpacity style={{ minHeight: 44, minWidth: 44, alignItems: 'center', justifyContent: 'center' }}
        onPress={() => setDismissed(true)}
        hitSlop={{ top: 8, bottom: 8, left: 8, right: 8 }}
        accessibilityRole="button"
        accessibilityLabel="Dispensar aviso"
      >
        <Text style={styles.close}>✕</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    borderRadius: 12,
    marginHorizontal: 16,
    marginBottom: 12,
    paddingVertical: 10,
    paddingHorizontal: 14,
  },
  main: { flex: 1 },
  text: { color: colors.textSubtle, fontSize: 13 },
  action: { color: colors.primaryLight, fontWeight: '600' },
  close: { color: colors.textMuted, fontSize: 14, paddingLeft: 12 },
});
