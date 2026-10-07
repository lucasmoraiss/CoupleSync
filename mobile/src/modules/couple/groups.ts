// Um usuário em vários grupos: regras de apresentação e de navegação. Lógica pura, coberta por
// __tests__/groups.test.ts. Quem decide o que o usuário pode ver é sempre o servidor; aqui só se escolhe o texto.
import type { MyGroupResponse, MyGroupsResponse } from '@/types/api';

/** O grupo ativo segundo o servidor, ou null (sem grupo, ou nenhum ativo). */
export function activeGroupOf(response: MyGroupsResponse | null | undefined): MyGroupResponse | null {
  if (!response) return null;
  return response.groups.find((group) => group.coupleId === response.activeCoupleId) ?? null;
}

/** Ainda cabe mais um grupo (criar ou entrar)? Sem a lista (servidor antigo, erro) não se bloqueia nada aqui. */
export function canAddGroup(response: MyGroupsResponse | null | undefined): boolean {
  if (!response) return true;
  return response.groups.length < response.maxGroups;
}

export function groupCountText(response: MyGroupsResponse): string {
  const count = response.groups.length;
  if (count >= response.maxGroups) {
    return `Você já participa de ${response.maxGroups} grupos, que é o máximo. Saia de um deles para criar ou entrar em outro.`;
  }
  return count === 1
    ? `Você participa de 1 grupo (o máximo é ${response.maxGroups}).`
    : `Você participa de ${count} grupos (o máximo é ${response.maxGroups}).`;
}

export function groupRoleText(group: MyGroupResponse): string {
  const role = group.isOwner ? 'Você administra' : 'Você participa';
  return group.isActive ? `${role} · grupo ativo` : role;
}

/** Como o leitor de tela anuncia a linha de um grupo na lista. */
export function groupAccessibilityLabel(group: MyGroupResponse): string {
  return group.isActive ? `${group.name}, grupo ativo` : `Trocar para ${group.name}`;
}

/**
 * Para onde vão as notificações bancárias capturadas e os lançamentos manuais. A regra do app: cada um vai para
 * o grupo que está ativo no momento em que é enviado; o que ainda não tinha sido enviado quando o grupo muda é
 * descartado, nunca lançado no outro grupo.
 */
export function captureDestinationText(response: MyGroupsResponse | null | undefined): string {
  const active = activeGroupOf(response);
  if (!response || !active) {
    return 'As notificações capturadas são lançadas no grupo que estiver ativo no momento em que chegam.';
  }
  if (response.groups.length === 1) {
    return `As notificações capturadas são lançadas no seu grupo (${active.name}).`;
  }
  return `As notificações capturadas são lançadas no grupo ativo no momento em que chegam. Agora: ${active.name}. Ao trocar de grupo, as próximas vão para o grupo novo.`;
}

/**
 * O que o servidor fará com o grupo ativo se o usuário sair de `leavingCoupleId`: saindo do ativo, o vínculo
 * mais antigo que restar passa a ser o ativo. Devolve o grupo que ficará ativo, ou null (nenhum).
 */
export function activeGroupAfterLeaving(
  response: MyGroupsResponse | null | undefined,
  leavingCoupleId: string,
): MyGroupResponse | null {
  if (!response) return null;
  const remaining = response.groups.filter((group) => group.coupleId !== leavingCoupleId);
  if (response.activeCoupleId !== leavingCoupleId) {
    return remaining.find((group) => group.coupleId === response.activeCoupleId) ?? null;
  }
  return [...remaining].sort((a, b) => a.joinedAtUtc.localeCompare(b.joinedAtUtc))[0] ?? null;
}

/** Frase extra da confirmação de saída para quem tem outros grupos. */
export function leaveFollowUpText(response: MyGroupsResponse | null | undefined, leavingCoupleId: string): string {
  const next = activeGroupAfterLeaving(response, leavingCoupleId);
  return next ? ` Depois de sair, o grupo ativo passa a ser: ${next.name}.` : '';
}

export type AfterLeave = 'home' | 'setup';

/** Depois de sair: com outro grupo ativo o app recarrega nele; sem grupo ativo vai para a escolha/criação de grupo. */
export function destinationAfterLeave(activeCoupleId: string | null | undefined): AfterLeave {
  return activeCoupleId ? 'home' : 'setup';
}

/** O seletor do painel só aparece para quem tem mais de um grupo: com um grupo só, o painel fica como sempre foi. */
export function shouldShowGroupPill(response: MyGroupsResponse | null | undefined): boolean {
  return !!response && response.groups.length > 1 && activeGroupOf(response) !== null;
}

/**
 * Aviso mostrado uma vez, no momento em que o grupo ativo muda. Com a captura de notificações ligada, diz também
 * para onde as próximas notificações bancárias vão (é o efeito da troca que o usuário não vê na tela).
 */
export function groupChangeNotice(groupName: string | null | undefined, captureOn: boolean): string {
  if (!groupName) {
    return captureOn
      ? 'Você mudou de grupo. As notificações bancárias capturadas a partir de agora vão para o grupo ativo.'
      : 'Você mudou de grupo.';
  }
  return captureOn
    ? `Grupo ativo: ${groupName}. As notificações bancárias capturadas a partir de agora vão para este grupo.`
    : `Grupo ativo: ${groupName}`;
}

export type MainAreaGate = 'wait' | 'login' | 'group-setup' | 'app';

/**
 * O que a área principal (abas com dados do grupo) faz: sem grupo ativo nenhuma tela de dados é montada, então
 * nenhuma consulta de grupo sai com um token sem grupo.
 */
export function mainAreaGate(state: {
  readonly hydrated: boolean;
  readonly accessToken: string | null;
  readonly coupleId: string | null;
}): MainAreaGate {
  if (!state.hydrated) return 'wait';
  if (!state.accessToken) return 'login';
  if (!state.coupleId) return 'group-setup';
  return 'app';
}
