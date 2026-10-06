import {
  activeGroupAfterLeaving,
  activeGroupOf,
  canAddGroup,
  captureDestinationText,
  destinationAfterLeave,
  groupAccessibilityLabel,
  groupCountText,
  groupRoleText,
  groupChangeNotice,
  leaveFollowUpText,
  mainAreaGate,
  shouldShowGroupPill,
} from '../groups';
import type { MyGroupResponse, MyGroupsResponse } from '@/types/api';

function group(coupleId: string, name: string, joinedAtUtc: string, extra: Partial<MyGroupResponse> = {}): MyGroupResponse {
  return { coupleId, name, joinedAtUtc, isOwner: false, isActive: false, members: [], ...extra };
}

const own = group('g-own', 'Grupo só seu', '2026-01-01T12:00:00Z', { isOwner: true });
const withBruno = group('g-bruno', 'Grupo com Bruno', '2026-03-01T12:00:00Z', { isActive: true });
const withCarla = group('g-carla', 'Grupo com Carla', '2026-02-01T12:00:00Z');

const three: MyGroupsResponse = { activeCoupleId: 'g-bruno', maxGroups: 5, groups: [own, withCarla, withBruno] };
const one: MyGroupsResponse = { activeCoupleId: 'g-bruno', maxGroups: 5, groups: [withBruno] };

describe('activeGroupOf', () => {
  it('é o grupo que o servidor diz estar ativo', () => {
    expect(activeGroupOf(three)).toBe(withBruno);
  });

  it('é null sem lista, sem grupo ativo, ou quando o ativo não está entre os grupos do usuário', () => {
    expect(activeGroupOf(undefined)).toBeNull();
    expect(activeGroupOf({ ...three, activeCoupleId: null })).toBeNull();
    expect(activeGroupOf({ ...three, activeCoupleId: 'g-de-outra-pessoa' })).toBeNull();
  });
});

describe('limite de grupos', () => {
  it('cabe mais um enquanto não chegou ao máximo', () => {
    expect(canAddGroup(three)).toBe(true);
    expect(groupCountText(three)).toBe('Você participa de 3 grupos (o máximo é 5).');
    expect(groupCountText(one)).toBe('Você participa de 1 grupo (o máximo é 5).');
  });

  it('no máximo não cabe, e o texto explica o que fazer', () => {
    const full: MyGroupsResponse = { ...three, maxGroups: 3 };
    expect(canAddGroup(full)).toBe(false);
    expect(groupCountText(full)).toBe(
      'Você já participa de 3 grupos, que é o máximo. Saia de um deles para criar ou entrar em outro.',
    );
  });

  it('sem a lista (servidor antigo) o app não bloqueia: quem decide é o servidor', () => {
    expect(canAddGroup(undefined)).toBe(true);
  });
});

describe('linha de um grupo na lista', () => {
  it('diz o papel e marca o ativo', () => {
    expect(groupRoleText(own)).toBe('Você administra');
    expect(groupRoleText(withBruno)).toBe('Você participa · grupo ativo');
  });

  it('o leitor de tela anuncia o ativo ou a ação de trocar', () => {
    expect(groupAccessibilityLabel(withBruno)).toBe('Grupo com Bruno, grupo ativo');
    expect(groupAccessibilityLabel(own)).toBe('Trocar para Grupo só seu');
  });
});

describe('captureDestinationText (para onde vão as notificações capturadas)', () => {
  it('com vários grupos nomeia o ativo e avisa que a troca muda o destino', () => {
    expect(captureDestinationText(three)).toBe(
      'As notificações capturadas são lançadas no grupo ativo no momento em que chegam. Agora: Grupo com Bruno. Ao trocar de grupo, as próximas vão para o grupo novo.',
    );
  });

  it('com um grupo só, diz qual é', () => {
    expect(captureDestinationText(one)).toBe('As notificações capturadas são lançadas no seu grupo (Grupo com Bruno).');
  });

  it('sem saber o grupo ativo, explica a regra sem inventar um nome', () => {
    const generic = 'As notificações capturadas são lançadas no grupo que estiver ativo no momento em que chegam.';
    expect(captureDestinationText(undefined)).toBe(generic);
    expect(captureDestinationText({ ...three, activeCoupleId: null })).toBe(generic);
  });
});

describe('sair de um grupo', () => {
  it('saindo do ativo, o vínculo mais antigo que resta passa a ser o ativo', () => {
    expect(activeGroupAfterLeaving(three, 'g-bruno')).toBe(own);
    expect(leaveFollowUpText(three, 'g-bruno')).toBe(' Depois de sair, o grupo ativo passa a ser: Grupo só seu.');
  });

  it('saindo de outro grupo, o ativo continua o mesmo', () => {
    expect(activeGroupAfterLeaving(three, 'g-own')).toBe(withBruno);
  });

  it('saindo do único grupo não sobra grupo ativo nem frase extra', () => {
    expect(activeGroupAfterLeaving(one, 'g-bruno')).toBeNull();
    expect(leaveFollowUpText(one, 'g-bruno')).toBe('');
    expect(leaveFollowUpText(undefined, 'g-bruno')).toBe('');
  });

  it('depois de sair: com outro grupo ativo vai ao painel; sem grupo ativo (ou servidor antigo) vai escolher/criar', () => {
    expect(destinationAfterLeave('g-own')).toBe('home');
    expect(destinationAfterLeave(null)).toBe('setup');
    expect(destinationAfterLeave(undefined)).toBe('setup');
  });
});

describe('shouldShowGroupPill (seletor no painel)', () => {
  it('quem tem um grupo só não vê o seletor: o painel fica como sempre foi', () => {
    expect(shouldShowGroupPill(one)).toBe(false);
  });

  it('aparece a partir do segundo grupo', () => {
    expect(shouldShowGroupPill(three)).toBe(true);
  });

  it('não aparece sem a lista ou sem grupo ativo', () => {
    expect(shouldShowGroupPill(undefined)).toBe(false);
    expect(shouldShowGroupPill({ ...three, activeCoupleId: null })).toBe(false);
  });
});

describe('groupChangeNotice (aviso no momento em que o grupo ativo muda)', () => {
  it('com a captura ligada avisa para onde as próximas notificações bancárias vão', () => {
    expect(groupChangeNotice('Grupo com Bruno', true)).toBe(
      'Grupo ativo: Grupo com Bruno. As notificações bancárias capturadas a partir de agora vão para este grupo.',
    );
  });

  it('com a captura desligada só diz qual é o grupo ativo', () => {
    expect(groupChangeNotice('Grupo com Bruno', false)).toBe('Grupo ativo: Grupo com Bruno');
  });

  it('sem saber o nome do grupo o aviso continua correto', () => {
    expect(groupChangeNotice(undefined, true)).toBe(
      'Você mudou de grupo. As notificações bancárias capturadas a partir de agora vão para o grupo ativo.',
    );
    expect(groupChangeNotice(undefined, false)).toBe('Você mudou de grupo.');
  });
});

describe('mainAreaGate (as telas com dados do grupo só existem com um grupo ativo)', () => {
  it('espera a sessão ser lida', () => {
    expect(mainAreaGate({ hydrated: false, accessToken: 'a', coupleId: 'g' })).toBe('wait');
  });

  it('sem sessão vai para o login', () => {
    expect(mainAreaGate({ hydrated: true, accessToken: null, coupleId: null })).toBe('login');
  });

  it('com sessão mas sem grupo ativo vai escolher ou criar um grupo, sem montar tela nenhuma de dados', () => {
    expect(mainAreaGate({ hydrated: true, accessToken: 'a', coupleId: null })).toBe('group-setup');
  });

  it('com sessão e grupo ativo mostra o app', () => {
    expect(mainAreaGate({ hydrated: true, accessToken: 'a', coupleId: 'g' })).toBe('app');
  });
});
