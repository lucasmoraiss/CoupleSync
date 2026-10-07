import React, { useEffect, useState } from 'react';
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
import { router, useLocalSearchParams } from 'expo-router';
import { coupleApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { logout } from '@/services/logout';
import { applyGroupSession } from '@/modules/couple/groupSession';
import { useSessionStore } from '@/state/sessionStore';
import { useMyGroups } from '@/modules/couple/useMyGroups';
import { canAddGroup, groupChangeNotice, groupCountText } from '@/modules/couple/groups';
import { isCaptureAllowedNow } from '@/modules/privacy/consentStore';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { GroupList } from '@/components/GroupSwitcher';
import { colors } from '@/theme';

export default function CoupleSetupScreen() {
  const [mode, setMode] = useState<'choose' | 'create' | 'join'>('choose');
  const [joinCode, setJoinCode] = useState('');
  const [loading, setLoading] = useState(false);
  const [createdCode, setCreatedCode] = useState<string | null>(null);
  // ?add=1: o usuário já tem grupo e veio criar ou entrar em mais um (há para onde voltar).
  const { add } = useLocalSearchParams<{ add?: string }>();
  // Quem chega aqui pode já participar de grupos (foi removido do grupo ativo, ou quer mais um): eles aparecem
  // para escolha, e o limite de grupos vale antes de oferecer criar ou entrar.
  const { data: myGroups } = useMyGroups();
  const hasGroups = (myGroups?.groups.length ?? 0) > 0;
  const mayAdd = canAddGroup(myGroups);

  // O aparelho não sabe de grupo nenhum, mas o token pode já ser de um grupo válido (ex.: o app foi fechado antes
  // de guardar o grupo depois do login). O servidor responde pelo grupo do token; se houver, é só seguir nele.
  const sessionCoupleId = useSessionStore((state) => state.coupleId);
  useEffect(() => {
    if (sessionCoupleId) return;
    let cancelled = false;
    coupleApiClient
      .getMyCouple()
      .then(async ({ data }) => {
        if (cancelled) return;
        await useSessionStore.getState().setCoupleId(data.coupleId);
        if (!cancelled) router.replace('/' as any);
      })
      .catch(() => undefined); // sem grupo no token: a pessoa escolhe, cria ou entra em um
    return () => {
      cancelled = true;
    };
  }, [sessionCoupleId]);

  // Quem já tinha grupo e passa a outro (criado ou no qual entrou) é avisado uma vez de que, com a captura ligada,
  // as próximas notificações bancárias vão para o grupo novo. No primeiro grupo não há o que avisar.
  const announceGroupChange = () => {
    if (hasGroups && isCaptureAllowedNow()) {
      showToastGlobal(groupChangeNotice(undefined, true), 'success', 6000);
    }
  };

  const handleCreate = async () => {
    setLoading(true);
    try {
      const { data } = await coupleApiClient.create();
      // O grupo novo passa a ser o ativo: guarda o token dele e esquece o que era do grupo anterior, se havia.
      await applyGroupSession({ accessToken: data.accessToken, refreshToken: data.refreshToken, coupleId: data.coupleId });
      announceGroupChange();
      setCreatedCode(data.joinCode);
      setMode('create');
    } catch (err: any) {
      Alert.alert('Erro', getApiErrorMessage(err, 'Erro ao criar o grupo. Tente novamente.'));
    } finally {
      setLoading(false);
    }
  };

  const handleJoin = async () => {
    if (!joinCode.trim()) {
      Alert.alert('Código vazio', 'Cole o código de convite do grupo.');
      return;
    }

    setLoading(true);
    try {
      const { data } = await coupleApiClient.join({ joinCode: joinCode.trim().toUpperCase() });
      // O grupo em que entrou passa a ser o ativo: guarda o token dele e esquece o que era do grupo anterior.
      await applyGroupSession({ accessToken: data.accessToken, refreshToken: data.refreshToken, coupleId: data.coupleId });
      announceGroupChange();
      router.replace('/' as any);
    } catch (err: any) {
      Alert.alert('Erro', getApiErrorMessage(err, 'Erro ao entrar no grupo. Tente novamente.'));
    } finally {
      setLoading(false);
    }
  };

  // Chega-se aqui com a sessão aberta e sem grupo (saiu do último, foi removido): tem de haver como sair da conta.
  const handleLogout = () => {
    Alert.alert('Sair da conta', 'Deseja realmente sair da conta? Você também será desconectado dos outros aparelhos em que usa esta conta.', [
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

  const [copied, setCopied] = useState(false);

  const handleCopyCode = async () => {
    if (createdCode) {
      await Clipboard.setStringAsync(createdCode);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    }
  };

  const goToHome = () => {
    router.replace('/' as any);
  };

  // Show the join code after creating a group
  if (createdCode) {
    return (
      <View style={styles.container}>
        <ScrollView contentContainerStyle={styles.scrollContent}>
          <View style={styles.header}>
            <Text style={styles.emoji}>🎉</Text>
            <Text style={styles.title} accessibilityRole="header">Grupo criado!</Text>
            <Text style={styles.subtitle}>
              Compartilhe este código com quem vai participar do grupo
            </Text>
          </View>

          <View style={styles.codeCard}>
            <Text style={styles.codeLabel}>Código de convite</Text>
            <Text style={styles.code} selectable>{createdCode}</Text>

            <TouchableOpacity
              style={styles.copyButton}
              onPress={handleCopyCode}
              activeOpacity={0.7}
              accessibilityLabel="Copiar código do grupo"
              accessibilityRole="button"
            >
              <Text style={styles.copyButtonText}>
                {copied ? '✓ Copiado!' : 'Copiar código'}
              </Text>
            </TouchableOpacity>

            <Text style={styles.codeHint}>
              Quem receber o código deve usá-lo para entrar no grupo
            </Text>
          </View>

          <TouchableOpacity
            style={styles.button}
            onPress={goToHome}
            activeOpacity={0.8}
            accessibilityLabel="Ir para o Painel"
            accessibilityRole="button"
          >
            <Text style={styles.buttonText}>Ir para o Painel</Text>
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
            <Text style={styles.title} accessibilityRole="header">Entrar no grupo</Text>
            <Text style={styles.subtitle}>
              Cole o código de convite que você recebeu
            </Text>
          </View>

          <View style={styles.form}>
            <TextInput accessibilityLabel="Código de convite do grupo"
              style={styles.codeInput}
              placeholder="XXXXXXXX"
              placeholderTextColor={colors.placeholder}
              value={joinCode}
              onChangeText={setJoinCode}
              autoCapitalize="characters"
              maxLength={8}
              editable={!loading}
              textAlign="center"
            />

            <TouchableOpacity
              style={[styles.button, loading && styles.buttonDisabled]}
              onPress={handleJoin}
              disabled={loading}
              activeOpacity={0.8}
              accessibilityLabel="Confirmar entrada no grupo"
              accessibilityRole="button"
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
              accessibilityLabel="Voltar para escolha de modo"
              accessibilityRole="button"
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
          <Text style={styles.title} accessibilityRole="header">Vamos configurar</Text>
          <Text style={styles.subtitle}>
            Crie um grupo ou entre com um código de convite
          </Text>
        </View>

        {hasGroups ? (
          <View style={styles.groupsCard}>
            <Text style={styles.groupsTitle} accessibilityRole="header">Seus grupos</Text>
            <Text style={styles.groupsHint}>Toque em um grupo para usar o app nele.</Text>
            <GroupList showAddButton={false} />
          </View>
        ) : null}

        {!mayAdd && myGroups ? <Text style={styles.limitText}>{groupCountText(myGroups)}</Text> : null}

        <View style={[styles.options, !mayAdd && styles.hidden]}>
          <TouchableOpacity
            style={[styles.optionCard, loading && styles.buttonDisabled]}
            onPress={handleCreate}
            disabled={loading}
            activeOpacity={0.8}
            accessibilityLabel="Criar grupo"
            accessibilityRole="button"
          >
            {loading ? (
              <ActivityIndicator color={colors.primary} />
            ) : (
              <>
                <Text style={styles.optionEmoji}>🏠</Text>
                <Text style={styles.optionTitle}>Criar grupo</Text>
                <Text style={styles.optionDesc}>
                  Comece e convide outra pessoa com um código
                </Text>
              </>
            )}
          </TouchableOpacity>

          <TouchableOpacity
            style={styles.optionCard}
            onPress={() => setMode('join')}
            disabled={loading}
            activeOpacity={0.8}
            accessibilityLabel="Entrar em um grupo com código de convite"
            accessibilityRole="button"
          >
            <Text style={styles.optionEmoji}>🔗</Text>
            <Text style={styles.optionTitle}>Entrar em um grupo</Text>
            <Text style={styles.optionDesc}>
              Tenho um código de convite
            </Text>
          </TouchableOpacity>
        </View>

        {add ? (
          <TouchableOpacity
            style={styles.linkButton}
            onPress={() => (router.canGoBack() ? router.back() : router.replace('/' as any))}
            disabled={loading}
            accessibilityLabel="Voltar sem criar nem entrar em outro grupo"
            accessibilityRole="button"
          >
            <Text style={styles.linkText}>← Voltar</Text>
          </TouchableOpacity>
        ) : null}

        <TouchableOpacity
          style={styles.linkButton}
          onPress={handleLogout}
          disabled={loading}
          accessibilityLabel="Sair da conta"
          accessibilityRole="button"
        >
          <Text style={styles.linkText}>Sair da conta</Text>
        </TouchableOpacity>
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
  hidden: {
    display: 'none',
  },
  groupsCard: {
    backgroundColor: colors.surface,
    borderRadius: 16,
    padding: 20,
    borderWidth: 1,
    borderColor: colors.border,
    marginBottom: 24,
  },
  groupsTitle: {
    fontSize: 18,
    fontWeight: '700',
    color: colors.text,
  },
  groupsHint: {
    fontSize: 13,
    color: colors.textMuted,
    marginTop: 2,
    marginBottom: 4,
  },
  limitText: {
    fontSize: 14,
    color: colors.textMuted,
    textAlign: 'center',
    marginBottom: 16,
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
    minHeight: 44,
    justifyContent: 'center',
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
    minHeight: 44,
    justifyContent: 'center',
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
