import React, { useState } from 'react';
import {
  View,
  Text,
  TextInput,
  TouchableOpacity,
  StyleSheet,
  ActivityIndicator,
  Alert,
  ScrollView,
} from 'react-native';
import * as Clipboard from 'expo-clipboard';
import { router } from 'expo-router';
import { coupleApiClient } from '@/services/apiClient';
import { useSessionStore } from '@/state/sessionStore';
import { colors } from '@/theme';

export default function CoupleSetupScreen() {
  const [mode, setMode] = useState<'choose' | 'create' | 'join'>('choose');
  const [joinCode, setJoinCode] = useState('');
  const [loading, setLoading] = useState(false);
  const [createdCode, setCreatedCode] = useState<string | null>(null);

  const handleCreate = async () => {
    setLoading(true);
    try {
      const { data } = await coupleApiClient.create();
      // Persist new JWT (backend returns a fresh token containing couple_id claim).
      await useSessionStore.getState().setAccessTokenAndCouple(data.accessToken, data.coupleId);
      setCreatedCode(data.joinCode);
      setMode('create');
    } catch (err: any) {
      const status = err?.response?.status;
      let msg = 'Erro ao criar casal. Tente novamente.';
      if (status === 409) msg = 'Você já faz parte de um casal.';
      Alert.alert('Erro', msg);
    } finally {
      setLoading(false);
    }
  };

  const handleJoin = async () => {
    if (!joinCode.trim()) {
      Alert.alert('Código vazio', 'Cole o código de convite do seu parceiro(a).');
      return;
    }

    setLoading(true);
    try {
      const { data } = await coupleApiClient.join({ joinCode: joinCode.trim().toUpperCase() });
      // Persist new JWT so the next requests send couple_id in the claim.
      await useSessionStore.getState().setAccessTokenAndCouple(data.accessToken, data.coupleId);
      router.replace('/' as any);
    } catch (err: any) {
      const status = err?.response?.status;
      let msg = 'Erro ao entrar no casal. Tente novamente.';
      if (status === 404) msg = 'Código de convite inválido ou expirado.';
      else if (status === 409) msg = 'Você já faz parte de um casal, ou o casal já está completo.';
      Alert.alert('Erro', msg);
    } finally {
      setLoading(false);
    }
  };

  const [copied, setCopied] = useState(false);

  const handleCopyCode = async () => {
    if (createdCode) {
      await Clipboard.setStringAsync(createdCode);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  const goToDashboard = () => {
    router.replace('/' as any);
  };

  // Show the join code after creating a couple
  if (createdCode) {
    return (
      <View style={styles.container}>
        <ScrollView contentContainerStyle={styles.scrollContent}>
          <View style={styles.header}>
            <Text style={styles.emoji}>🎉</Text>
            <Text style={styles.title}>Casal criado!</Text>
            <Text style={styles.subtitle}>
              Compartilhe este código com seu parceiro(a)
            </Text>
          </View>

          <View style={styles.codeCard}>
            <Text style={styles.codeLabel}>Código de convite</Text>
            <Text style={styles.code} selectable>{createdCode}</Text>

            <TouchableOpacity
              style={styles.copyButton}
              onPress={handleCopyCode}
              activeOpacity={0.7}
            >
              <Text style={styles.copyButtonText}>
                {copied ? '✓ Copiado!' : 'Copiar código'}
              </Text>
            </TouchableOpacity>

            <Text style={styles.codeHint}>
              O parceiro(a) deve usar este código para entrar no casal
            </Text>
          </View>

          <TouchableOpacity
            style={styles.button}
            onPress={goToDashboard}
            activeOpacity={0.8}
          >
            <Text style={styles.buttonText}>Ir para o Dashboard</Text>
          </TouchableOpacity>
        </ScrollView>
      </View>
    );
  }

  // Join mode — input the code
  if (mode === 'join') {
    return (
      <View style={styles.container}>
        <ScrollView contentContainerStyle={styles.scrollContent}>
          <View style={styles.header}>
            <Text style={styles.emoji}>🔗</Text>
            <Text style={styles.title}>Entrar no casal</Text>
            <Text style={styles.subtitle}>
              Cole o código que seu parceiro(a) compartilhou
            </Text>
          </View>

          <View style={styles.form}>
            <TextInput
              style={styles.codeInput}
              placeholder="XXXXXX"
              placeholderTextColor={colors.placeholder}
              value={joinCode}
              onChangeText={setJoinCode}
              autoCapitalize="characters"
              maxLength={6}
              editable={!loading}
              textAlign="center"
            />

            <TouchableOpacity
              style={[styles.button, loading && styles.buttonDisabled]}
              onPress={handleJoin}
              disabled={loading}
              activeOpacity={0.8}
            >
              {loading ? (
                <ActivityIndicator color={colors.white} />
              ) : (
                <Text style={styles.buttonText}>Entrar</Text>
              )}
            </TouchableOpacity>

            <TouchableOpacity
              style={styles.linkButton}
              onPress={() => setMode('choose')}
              disabled={loading}
            >
              <Text style={styles.linkText}>← Voltar</Text>
            </TouchableOpacity>
          </View>
        </ScrollView>
      </View>
    );
  }

  // Choose mode — create or join
  return (
    <View style={styles.container}>
      <ScrollView contentContainerStyle={styles.scrollContent}>
        <View style={styles.header}>
          <Text style={styles.emoji}>💑</Text>
          <Text style={styles.title}>Vamos configurar</Text>
          <Text style={styles.subtitle}>
            Crie um casal ou entre com um código de convite
          </Text>
        </View>

        <View style={styles.options}>
          <TouchableOpacity
            style={[styles.optionCard, loading && styles.buttonDisabled]}
            onPress={handleCreate}
            disabled={loading}
            activeOpacity={0.8}
          >
            {loading ? (
              <ActivityIndicator color={colors.primary} />
            ) : (
              <>
                <Text style={styles.optionEmoji}>🏠</Text>
                <Text style={styles.optionTitle}>Criar casal</Text>
                <Text style={styles.optionDesc}>
                  Comece e convide seu parceiro(a) com um código
                </Text>
              </>
            )}
          </TouchableOpacity>

          <TouchableOpacity
            style={styles.optionCard}
            onPress={() => setMode('join')}
            disabled={loading}
            activeOpacity={0.8}
          >
            <Text style={styles.optionEmoji}>🔗</Text>
            <Text style={styles.optionTitle}>Entrar em um casal</Text>
            <Text style={styles.optionDesc}>
              Tenho um código de convite do meu parceiro(a)
            </Text>
          </TouchableOpacity>
        </View>
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.background,
  },
  scrollContent: {
    flexGrow: 1,
    justifyContent: 'center',
    paddingHorizontal: 24,
    paddingVertical: 48,
  },
  header: {
    alignItems: 'center',
    marginBottom: 40,
  },
  emoji: {
    fontSize: 56,
    marginBottom: 12,
  },
  title: {
    fontSize: 28,
    fontWeight: '700',
    color: colors.text,
    letterSpacing: -0.5,
  },
  subtitle: {
    fontSize: 16,
    color: colors.textMuted,
    marginTop: 4,
    textAlign: 'center',
    paddingHorizontal: 20,
  },
  options: {
    gap: 16,
  },
  optionCard: {
    backgroundColor: colors.surface,
    borderRadius: 16,
    padding: 24,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
  },
  optionEmoji: {
    fontSize: 36,
    marginBottom: 12,
  },
  optionTitle: {
    fontSize: 18,
    fontWeight: '700',
    color: colors.text,
    marginBottom: 4,
  },
  optionDesc: {
    fontSize: 14,
    color: colors.textMuted,
    textAlign: 'center',
  },
  form: {
    width: '100%',
  },
  codeInput: {
    backgroundColor: colors.surface,
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 18,
    fontSize: 28,
    fontWeight: '700',
    color: colors.text,
    marginBottom: 24,
    borderWidth: 1,
    borderColor: colors.border,
    letterSpacing: 8,
  },
  button: {
    backgroundColor: colors.primary,
    borderRadius: 12,
    paddingVertical: 16,
    alignItems: 'center',
  },
  buttonDisabled: {
    opacity: 0.6,
  },
  buttonText: {
    color: colors.white,
    fontSize: 16,
    fontWeight: '700',
  },
  linkButton: {
    alignItems: 'center',
    marginTop: 24,
    paddingVertical: 8,
  },
  linkText: {
    color: colors.primaryLight,
    fontSize: 14,
    fontWeight: '600',
  },
  codeCard: {
    backgroundColor: colors.surface,
    borderRadius: 16,
    padding: 32,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: colors.border,
    marginBottom: 32,
  },
  codeLabel: {
    fontSize: 14,
    color: colors.textMuted,
    marginBottom: 8,
  },
  code: {
    fontSize: 36,
    fontWeight: '800',
    color: colors.primaryLight,
    letterSpacing: 6,
    marginBottom: 12,
  },
  copyButton: {
    backgroundColor: colors.border,
    borderRadius: 8,
    paddingHorizontal: 20,
    paddingVertical: 10,
    marginBottom: 8,
  },
  copyButtonText: {
    color: colors.primaryLight,
    fontSize: 14,
    fontWeight: '600',
  },
  codeHint: {
    fontSize: 13,
    color: colors.textDisabled,
    textAlign: 'center',
    marginTop: 4,
  },
});
