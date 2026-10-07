// Seletor de grupo: mostra o grupo ativo e deixa trocar para outro grupo do usuário.
//  - <GroupSwitcher />: botão compacto (painel) que abre a lista num modal;
//  - <GroupList />: a lista em si (tela do grupo, tela de escolha de grupo).
// As regras (textos, limite, ordem da troca) estão em src/modules/couple/{groups,activateGroup}.ts, com testes.
import React, { useState } from 'react';
import { ActivityIndicator, Alert, Modal, Pressable, StyleSheet, Text, TouchableOpacity, View } from 'react-native';
import { router } from 'expo-router';
import { useMutation } from '@tanstack/react-query';
import { Ionicons } from '@expo/vector-icons';
import { coupleApiClient } from '@/services/apiClient';
import { getApiErrorMessage } from '@/services/apiError';
import { showToastGlobal } from '@/components/Toast/ToastProvider';
import { isCaptureAllowedNow } from '@/modules/privacy/consentStore';
import { applyGroupSession } from '@/modules/couple/groupSession';
import { useMyGroups } from '@/modules/couple/useMyGroups';
import {
  activeGroupOf,
  canAddGroup,
  groupAccessibilityLabel,
  groupChangeNotice,
  groupCountText,
  groupRoleText,
  shouldShowGroupPill,
} from '@/modules/couple/groups';
import { colors } from '@/theme';
import type { MyGroupResponse } from '@/types/api';

/** Troca para o grupo escolhido: tokens novos, cache do grupo anterior esvaziado, telas recarregadas no painel. */
function useSwitchGroup(onSwitched?: () => void) {
  return useMutation({
    mutationFn: async (group: MyGroupResponse) => {
      const { data } = await coupleApiClient.switchTo(group.coupleId);
      await applyGroupSession({ accessToken: data.accessToken, refreshToken: data.refreshToken, coupleId: data.coupleId });
      return group;
    },
    onSuccess: (group) => {
      onSwitched?.();
      // Dito uma vez, na hora da troca: com a captura ligada, as próximas notificações bancárias vão para este grupo.
      showToastGlobal(groupChangeNotice(group.name, isCaptureAllowedNow()), 'success', 6000);
      router.replace('/' as any);
    },
    onError: (err) => Alert.alert('Erro', getApiErrorMessage(err, 'Não foi possível trocar de grupo.')),
  });
}

interface GroupListProps {
  /** Chamado depois de trocar ou ao seguir para criar/entrar (o modal usa para se fechar). */
  readonly onDone?: () => void;
  /** Mostra o botão de criar ou entrar em outro grupo (a tela de escolha de grupo já tem essas opções). */
  readonly showAddButton?: boolean;
}

export function GroupList({ onDone, showAddButton = true }: GroupListProps) {
  const { data } = useMyGroups();
  const switchGroup = useSwitchGroup(onDone);

  if (!data || data.groups.length === 0) return null;

  return (
    <View>
      {data.groups.map((group, index) => (
        <TouchableOpacity
          key={group.coupleId}
          style={[styles.row, index > 0 && styles.rowBorder]}
          onPress={() => switchGroup.mutate(group)}
          disabled={group.isActive || switchGroup.isPending}
          accessibilityRole="button"
          accessibilityLabel={groupAccessibilityLabel(group)}
          accessibilityState={{ selected: group.isActive, disabled: group.isActive || switchGroup.isPending }}
        >
          <View style={styles.rowInfo}>
            <Text style={styles.rowName}>{group.name}</Text>
            <Text style={styles.rowMeta}>{groupRoleText(group)}</Text>
          </View>
          {switchGroup.isPending && switchGroup.variables?.coupleId === group.coupleId ? (
            <ActivityIndicator color={colors.primaryLight} />
          ) : group.isActive ? (
            <Ionicons name="checkmark-circle" size={22} color={colors.success} />
          ) : (
            <Text style={styles.rowAction}>Trocar</Text>
          )}
        </TouchableOpacity>
      ))}

      <Text style={styles.count}>{groupCountText(data)}</Text>

      {showAddButton && canAddGroup(data) ? (
        <TouchableOpacity
          style={styles.addButton}
          onPress={() => {
            onDone?.();
            router.push('/(auth)/couple-setup?add=1' as any);
          }}
          disabled={switchGroup.isPending}
          accessibilityRole="button"
          accessibilityLabel="Criar ou entrar em outro grupo"
        >
          <Text style={styles.addButtonText}>Criar ou entrar em outro grupo</Text>
        </TouchableOpacity>
      ) : null}
    </View>
  );
}

export function GroupSwitcher() {
  const { data } = useMyGroups();
  const [open, setOpen] = useState(false);
  const active = activeGroupOf(data);

  // Só para quem tem mais de um grupo: com um grupo só o painel fica como sempre foi (criar ou entrar em outro
  // grupo fica na tela do grupo, em Configurações).
  if (!shouldShowGroupPill(data) || !active) return null;

  return (
    <>
      <TouchableOpacity
        style={styles.pill}
        onPress={() => setOpen(true)}
        accessibilityRole="button"
        accessibilityLabel={`Grupo ativo: ${active.name}. Toque para trocar de grupo.`}
      >
        <Ionicons name="people-outline" size={16} color={colors.primaryLight} />
        <Text style={styles.pillText} numberOfLines={1}>{active.name}</Text>
        <Ionicons name="chevron-down" size={16} color={colors.primaryLight} />
      </TouchableOpacity>

      <Modal visible={open} transparent animationType="fade" onRequestClose={() => setOpen(false)}>
        <Pressable style={styles.backdrop} onPress={() => setOpen(false)} accessibilityLabel="Fechar a lista de grupos" accessibilityRole="button">
          <Pressable style={styles.sheet} onPress={() => undefined} accessible={false}>
            <Text style={styles.sheetTitle} accessibilityRole="header">Seus grupos</Text>
            <GroupList onDone={() => setOpen(false)} />
            <TouchableOpacity
              style={styles.closeButton}
              onPress={() => setOpen(false)}
              accessibilityRole="button"
              accessibilityLabel="Fechar"
            >
              <Text style={styles.closeText}>Fechar</Text>
            </TouchableOpacity>
          </Pressable>
        </Pressable>
      </Modal>
    </>
  );
}

const styles = StyleSheet.create({
  pill: {
    flexDirection: 'row',
    alignItems: 'center',
    alignSelf: 'flex-start',
    gap: 6,
    minHeight: 44,
    maxWidth: '100%',
    paddingHorizontal: 14,
    borderRadius: 22,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    marginBottom: 16,
  },
  pillText: { flexShrink: 1, color: colors.text, fontSize: 14, fontWeight: '600' },
  backdrop: { flex: 1, backgroundColor: colors.overlay, justifyContent: 'center', paddingHorizontal: 20 },
  sheet: { backgroundColor: colors.surface, borderRadius: 16, borderWidth: 1, borderColor: colors.border, padding: 20 },
  sheetTitle: { fontSize: 18, fontWeight: '700', color: colors.text, marginBottom: 8 },
  row: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', minHeight: 56, paddingVertical: 10 },
  rowBorder: { borderTopWidth: 1, borderTopColor: colors.border },
  rowInfo: { flex: 1, paddingRight: 12 },
  rowName: { fontSize: 16, color: colors.text, fontWeight: '600' },
  rowMeta: { fontSize: 13, color: colors.textMuted, marginTop: 2 },
  rowAction: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  count: { fontSize: 13, color: colors.textMuted, marginTop: 12 },
  addButton: {
    minHeight: 44,
    justifyContent: 'center',
    alignItems: 'center',
    backgroundColor: colors.border,
    borderRadius: 8,
    paddingHorizontal: 20,
    paddingVertical: 12,
    marginTop: 12,
  },
  addButtonText: { color: colors.primaryLight, fontSize: 14, fontWeight: '600' },
  closeButton: { minHeight: 44, justifyContent: 'center', alignItems: 'center', marginTop: 8 },
  closeText: { color: colors.textMuted, fontSize: 14, fontWeight: '600' },
});
