// Tela do grupo: membros, código de convite com validade (sempre visível, com botão de copiar)
// e as ações sair / remover membro / gerar novo código, cada uma com confirmação.
import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  SafeAreaView,
  ScrollView,
  StyleSheet,
  Text,
  TouchableOpacity,
  View,
} from 'react-native';
import { router } from 'expo-router';
import * as Clipboard from 'expo-clipboard';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { coupleApiClient } from '@/services/apiClient';
import { getApiErrorMessage, isNoGroupError } from '@/services/apiError';
import { useSessionStore } from '@/state/sessionStore';
import { clearGroupScopedQueries } from '@/services/queryClient';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { GroupList } from '@/components/GroupSwitcher';
import { applyGroupSession } from '@/modules/couple/groupSession';
import { useMyGroups } from '@/modules/couple/useMyGroups';
import {
  activeGroupOf,
  destinationAfterLeave,
  groupChangeNotice,
  leaveConfirmationText,
  leaveFollowUpText,
  removeMemberConfirmationText,
} from '@/modules/couple/groups';
import { isCaptureAllowedNow } from '@/modules/privacy/consentStore';
import { describeJoinCodeValidity, isGroupOwner } from '@/modules/couple/group';
import { ErrorState } from '@/components/ErrorState';
import { LoadingState } from '@/components/LoadingState';
import { goToParent, useOnRefocus } from '@/navigation/resetOnFocus';
import { colors } from '@/theme';
import type { CoupleMemberResponse, GetCoupleMeResponse } from '@/types/api';

const QUERY_KEY = ['couple-me'] as const;

export default function GroupScreen() {
  const queryClient = useQueryClient();
  const userId = useSessionStore((s) => s.userId);
  const [copied, setCopied] = useState(false);
  const { data: myGroups, refetch: refetchMyGroups } = useMyGroups();
  const [refreshing, setRefreshing] = useState(false);

  const { data, error, isLoading, isError, refetch } = useQuery<GetCoupleMeResponse>({
    queryKey: QUERY_KEY,
    queryFn: () => coupleApiClient.getMyCouple().then((r) => r.data),
    // "Você não tem grupo" não muda ao tentar de novo.
    retry: (failureCount, err) => !isNoGroupError(err) && failureCount < 2,
  });

  // A tela é uma aba oculta e continua montada: membros, administrador, validade do código e a lista de grupos
  // mudam por ação de outras pessoas. Busca de novo a cada volta à tela e ao puxar para atualizar.
  const reload = useCallback(
    () => Promise.all([refetch({ cancelRefetch: false }), refetchMyGroups({ cancelRefetch: false })]),
    [refetch, refetchMyGroups],
  );
  useOnRefocus(() => {
    setCopied(false);
    void reload();
  });
  const handleRefresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await reload();
    } finally {
      setRefreshing(false);
    }
  }, [reload]);

  // O servidor diz que não há grupo (removido, ou grupo guardado velho): esquece o grupo e vai configurar outro.
  const noGroup = isNoGroupError(error);
  useEffect(() => {
    if (!noGroup) return;
    void useSessionStore.getState().clearCouple();
    // Nada do grupo antigo deve aparecer depois. Esta própria consulta fica: removê-la a refaria em laço.
    clearGroupScopedQueries(queryClient);
    router.replace('/(auth)/couple-setup' as any);
  }, [noGroup, queryClient]);

  const owner = isGroupOwner(data?.ownerUserId, userId);
  const validity = data?.joinCodeExpiresAtUtc ? describeJoinCodeValidity(data.joinCodeExpiresAtUtc) : null;

  const regenerate = useMutation({
    mutationFn: () => coupleApiClient.regenerateJoinCode().then((r) => r.data),
    onSuccess: (result) => {
      queryClient.setQueryData<GetCoupleMeResponse>(QUERY_KEY, (old) =>
        old ? { ...old, joinCode: result.joinCode, joinCodeExpiresAtUtc: result.joinCodeExpiresAtUtc } : old,
      );
    },
    onError: (err) => Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível gerar um novo código.')),
  });

  const removeMember = useMutation({
    mutationFn: (memberUserId: string) => coupleApiClient.removeMember(memberUserId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: QUERY_KEY }),
    onError: (err) => Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível remover o membro.')),
  });

  const leave = useMutation({
    mutationFn: () => coupleApiClient.leave().then((r) => r.data),
    onSuccess: async (result) => {
      // Tokens novos e nada do grupo antigo no aparelho; se o usuário tem outro grupo, o servidor já o ativou.
      await applyGroupSession({
        accessToken: result.accessToken,
        refreshToken: result.refreshToken,
        coupleId: result.activeCoupleId ?? null,
      });
      if (destinationAfterLeave(result.activeCoupleId) === 'home') {
        const next = myGroups?.groups.find((g) => g.coupleId === result.activeCoupleId);
        showToastGlobal(`Você saiu do grupo. ${groupChangeNotice(next?.name, isCaptureAllowedNow())}`, 'success', 6000);
        router.replace('/' as any);
      } else {
        router.replace('/(auth)/couple-setup' as any);
      }
    },
    onError: (err) => Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível sair do grupo.')),
  });

  const handleCopy = async () => {
    if (!data?.joinCode) return;
    await Clipboard.setStringAsync(data.joinCode);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const confirmRegenerate = () => {
    Alert.alert(
      'Gerar novo código',
      'O código atual deixa de funcionar na hora. Quem ainda não entrou precisará do código novo.',
      [
        { text: 'Cancelar', style: 'cancel' },
        { text: 'Gerar novo código', onPress: () => regenerate.mutate() },
      ],
    );
  };

  const confirmRemove = (member: CoupleMemberResponse) => {
    Alert.alert(
      'Remover membro',
      removeMemberConfirmationText(member.name),
      [
        { text: 'Cancelar', style: 'cancel' },
        { text: 'Remover', style: 'destructive', onPress: () => removeMember.mutate(member.userId) },
      ],
    );
  };

  const confirmLeave = () => {
    const onlyMember = (data?.members.length ?? 0) <= 1;
    const message = leaveConfirmationText({ onlyMember, owner });
    const followUp = data ? leaveFollowUpText(myGroups, data.coupleId) : '';
    Alert.alert('Sair do grupo', message + followUp, [
      { text: 'Cancelar', style: 'cancel' },
      { text: 'Sair do grupo', style: 'destructive', onPress: () => leave.mutate() },
    ]);
  };

  if (isLoading || noGroup) return <LoadingState />;
  if (isError || !data) {
    return (
      <SafeAreaView style={styles.container}>
        <ErrorState message="Não foi possível carregar o grupo." onRetry={() => refetch()} />
      </SafeAreaView>
    );
  }

  const busy = regenerate.isPending || removeMember.isPending || leave.isPending;

  return (
    <SafeAreaView style={styles.container}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={handleRefresh} tintColor={colors.primaryLight} />}
      >
        <TouchableOpacity onPress={() => goToParent('settings/group')} accessibilityRole="button" accessibilityLabel="Voltar">
          <Text style={styles.back}>← Voltar</Text>
        </TouchableOpacity>
        <Text style={styles.title} accessibilityRole="header">Grupo</Text>

        {myGroups && myGroups.groups.length > 0 ? (
          <>
            <Text style={[styles.sectionTitle, styles.firstSection]} accessibilityRole="header">Seus grupos</Text>
            <View style={styles.card}>
              <GroupList />
            </View>
            <Text style={styles.sectionTitle} accessibilityRole="header">
              {activeGroupOf(myGroups) ? `Grupo ativo: ${activeGroupOf(myGroups)!.name}` : 'Grupo ativo'}
            </Text>
          </>
        ) : null}

        <View style={styles.card}>
          <Text style={styles.cardLabel}>Código de convite</Text>
          <Text style={styles.code} selectable>{data.joinCode}</Text>
          {validity?.text ? (
            <Text style={[styles.validity, validity.expired && styles.validityExpired]}>{validity.text}</Text>
          ) : null}
          <TouchableOpacity
            style={styles.secondaryButton}
            onPress={handleCopy}
            accessibilityRole="button"
            accessibilityLabel="Copiar código de convite"
          >
            <Text style={styles.secondaryButtonText}>{copied ? '✓ Copiado!' : 'Copiar código'}</Text>
          </TouchableOpacity>
          {owner ? (
            <TouchableOpacity
              style={[styles.secondaryButton, busy && styles.disabled]}
              onPress={confirmRegenerate}
              disabled={busy}
              accessibilityRole="button"
              accessibilityLabel="Gerar novo código de convite"
            >
              {regenerate.isPending ? (
                <ActivityIndicator color={colors.primaryLight} />
              ) : (
                <Text style={styles.secondaryButtonText}>Gerar novo código</Text>
              )}
            </TouchableOpacity>
          ) : null}
        </View>

        <Text style={styles.sectionTitle} accessibilityRole="header">Membros</Text>
        <View style={styles.card}>
          {data.members.map((member, index) => {
            const isMe = member.userId === userId;
            const isOwnerMember = member.userId === data.ownerUserId;
            return (
              <View key={member.userId} style={[styles.memberRow, index > 0 && styles.memberRowBorder]}>
                <View style={styles.memberInfo}>
                  <Text style={styles.memberName}>
                    {member.name}
                    {isMe ? ' (você)' : ''}
                  </Text>
                  <Text style={styles.memberMeta}>
                    {member.email}
                    {isOwnerMember ? ' · administrador' : ''}
                  </Text>
                </View>
                {owner && !isMe ? (
                  <TouchableOpacity style={{ minHeight: 44, justifyContent: 'center' }}
                    onPress={() => confirmRemove(member)}
                    disabled={busy}
                    accessibilityRole="button"
                    accessibilityLabel={`Remover ${member.name} do grupo`}
                  >
                    <Text style={styles.removeText}>Remover</Text>
                  </TouchableOpacity>
                ) : null}
              </View>
            );
          })}
        </View>

        <TouchableOpacity
          style={[styles.leaveButton, busy && styles.disabled]}
          onPress={confirmLeave}
          disabled={busy}
          accessibilityRole="button"
          accessibilityLabel="Sair do grupo"
        >
          {leave.isPending ? <ActivityIndicator color={colors.error} /> : <Text style={styles.leaveText}>Sair do grupo</Text>}
        </TouchableOpacity>
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: colors.background },
  content: { paddingHorizontal: 20, paddingTop: 24, paddingBottom: 40 },
  back: { minHeight: 44, textAlignVertical: 'center', color: colors.primaryLight, fontSize: 14, fontWeight: '600', marginBottom: 12 },
  title: { fontSize: 26, fontWeight: '700', color: colors.text, marginBottom: 20 },
  sectionTitle: { fontSize: 16, fontWeight: '700', color: colors.text, marginTop: 24, marginBottom: 8 },
  firstSection: { marginTop: 0 },
  card: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 20 },
  cardLabel: { fontSize: 14, color: colors.textMuted, marginBottom: 8, textAlign: 'center' },
  code: { fontSize: 32, fontWeight: '800', color: colors.primaryLight, letterSpacing: 5, textAlign: 'center' },
  validity: { fontSize: 13, color: colors.textMuted, textAlign: 'center', marginTop: 6, marginBottom: 12 },
  validityExpired: { color: colors.errorLight },
  secondaryButton: {
    minHeight: 44,
    justifyContent: 'center',
    backgroundColor: colors.border,
    borderRadius: 8,
    paddingHorizontal: 20,
    paddingVertical: 12,
    alignItems: 'center',
    marginTop: 8,
  },
  secondaryButtonText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  memberRow: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingVertical: 12 },
  memberRowBorder: { borderTopWidth: 1, borderTopColor: colors.border },
  memberInfo: { flex: 1, paddingRight: 12 },
  memberName: { fontSize: 16, color: colors.text, fontWeight: '600' },
  memberMeta: { fontSize: 13, color: colors.textMuted, marginTop: 2 },
  removeText: { color: colors.error, fontSize: 14, fontWeight: '600' },
  leaveButton: {
    marginTop: 32,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.error,
    paddingVertical: 14,
    alignItems: 'center',
  },
  leaveText: { color: colors.error, fontSize: 16, fontWeight: '700' },
  disabled: { opacity: 0.5 },
});
