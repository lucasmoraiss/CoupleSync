// Open Finance (issue #24): regras puras do wizard de conexão com o Meu Pluggy. Tudo aqui é inventado
// ("Banco Exemplo", ids falsos); nenhum dado bancário real.
import {
  DASHBOARD_URL,
  DEFAULT_CONNECTION_LABEL,
  EMPTY_PROGRESS,
  ITEM_STEPS,
  DASHBOARD_STEPS,
  MEU_PLUGGY_STEPS,
  MEU_PLUGGY_URL,
  WIZARD_DONE_MESSAGE,
  UNAVAILABLE_TITLE,
  canContinueFromBanks,
  canCreateConnection,
  canFinishWizard,
  canTestCredentials,
  canVerifyItem,
  connectionControls,
  connectionErrorHelp,
  connectionStatusLabel,
  existingConnectionAfterCreateError,
  itemStatusNote,
  statusWhenRouteMissing,
  accountBalanceText,
  describeAccount,
  describeItemAccounts,
  isOpenFinanceAvailable,
  myConnectionOf,
  parseProgress,
  progressStorageKey,
  sameCredentials,
  serializeProgress,
  startingStep,
  type WizardProgress,
} from '../wizard';
import type { BankAccountResponse, BankConnectionResponse, OpenFinanceStatusResponse } from '@/types/api';

const account = (over: Partial<BankAccountResponse>): BankAccountResponse => ({
  id: 'acc-1',
  type: 'BANK',
  subtype: 'CHECKING_ACCOUNT',
  name: 'Conta Corrente',
  marketingName: null,
  numberMasked: '1234',
  currency: 'BRL',
  balance: 10,
  balanceAtUtc: '2026-10-07T12:00:00Z',
  creditLimit: null,
  availableCreditLimit: null,
  balanceCloseDate: null,
  balanceDueDate: null,
  minimumPayment: null,
  brand: null,
  syncEnabled: true,
  ...over,
});

const connection = (over: Partial<BankConnectionResponse>): BankConnectionResponse => ({
  id: 'conn-1',
  label: 'Meus bancos',
  userId: 'user-1',
  userName: 'Ana',
  isMine: true,
  status: 'Active',
  clientIdHint: '0a1b',
  historyMonths: 3,
  lastSyncAtUtc: null,
  lastErrorCode: null,
  lastErrorMessage: null,
  createdAtUtc: '2026-10-07T12:00:00Z',
  items: [],
  ...over,
});

describe('passo 4: "Verificar" só com o Item ID preenchido', () => {
  it('desabilitado com o campo vazio ou só com espaços', () => {
    expect(canVerifyItem('', false)).toBe(false);
    expect(canVerifyItem('   ', false)).toBe(false);
    expect(canVerifyItem('\n\t', false)).toBe(false);
  });

  it('habilitado com um Item ID preenchido', () => {
    expect(canVerifyItem('a1b2c3d4-0000-4000-8000-000000000001', false)).toBe(true);
    expect(canVerifyItem('  a1b2c3d4-0000-4000-8000-000000000001  ', false)).toBe(true);
  });

  it('desabilitado enquanto uma verificação está em andamento', () => {
    expect(canVerifyItem('a1b2c3d4-0000-4000-8000-000000000001', true)).toBe(false);
  });

  it('"Concluir" só depois de pelo menos um banco verificado', () => {
    expect(canFinishWizard([])).toBe(false);
    expect(canFinishWizard([{ verified: false }, { verified: false }])).toBe(false);
    expect(canFinishWizard([{ verified: false }, { verified: true }])).toBe(true);
  });
});

describe('passos 2 e 3: o que habilita cada botão', () => {
  it('passo 2 só continua com a caixa "Conectei meus bancos" marcada', () => {
    expect(canContinueFromBanks(false)).toBe(false);
    expect(canContinueFromBanks(true)).toBe(true);
  });

  it('"Testar credenciais" precisa do Client ID e do Client Secret', () => {
    expect(canTestCredentials('', '', false)).toBe(false);
    expect(canTestCredentials('fake-client-id', '  ', false)).toBe(false);
    expect(canTestCredentials('  ', 'fake-secret', false)).toBe(false);
    expect(canTestCredentials('fake-client-id', 'fake-secret', false)).toBe(true);
    expect(canTestCredentials('fake-client-id', 'fake-secret', true)).toBe(false);
  });

  it('só continua com as MESMAS credenciais que passaram no teste', () => {
    const tested = { clientId: 'fake-client-id', clientSecret: 'fake-secret' };
    expect(canCreateConnection('Meus bancos', 'fake-client-id', 'fake-secret', tested, false)).toBe(true);
    expect(canCreateConnection('Meus bancos', ' fake-client-id ', ' fake-secret ', tested, false)).toBe(true);
    expect(canCreateConnection('Meus bancos', 'fake-client-id', 'fake-other-secret', tested, false)).toBe(false);
    expect(canCreateConnection('Meus bancos', 'fake-other-id', 'fake-secret', tested, false)).toBe(false);
    expect(canCreateConnection('Meus bancos', 'fake-client-id', 'fake-secret', null, false)).toBe(false);
    expect(canCreateConnection('   ', 'fake-client-id', 'fake-secret', tested, false)).toBe(false);
    expect(canCreateConnection('Meus bancos', 'fake-client-id', 'fake-secret', tested, true)).toBe(false);
    expect(sameCredentials(tested, 'fake-client-id', 'fake-secret')).toBe(true);
    expect(DEFAULT_CONNECTION_LABEL.trim().length).toBeGreaterThan(0);
  });
});

describe('contas encontradas', () => {
  it('descreve o banco e as contas como "Banco · conta corrente ····1234 · cartão ····5678"', () => {
    const text = describeItemAccounts('Banco Exemplo', [
      account({ subtype: 'CHECKING_ACCOUNT', numberMasked: '1234' }),
      account({ id: 'acc-2', type: 'CREDIT', subtype: 'CREDIT_CARD', numberMasked: '5678' }),
    ]);
    expect(text).toBe('Banco Exemplo · conta corrente ····1234 · cartão ····5678');
  });

  it('poupança, conta sem número e tipos desconhecidos têm rótulo em português', () => {
    expect(describeAccount(account({ subtype: 'SAVINGS_ACCOUNT', numberMasked: '4321' }))).toBe('poupança ····4321');
    expect(describeAccount(account({ numberMasked: null }))).toBe('conta corrente');
    expect(describeAccount(account({ type: 'CREDIT', subtype: null, numberMasked: '9' }))).toBe('cartão ····9');
    expect(describeAccount(account({ type: 'BANK', subtype: null, numberMasked: '' }))).toBe('conta');
    expect(describeAccount(account({ type: 'LOAN', subtype: 'SOMETHING_NEW', numberMasked: '7777' }))).toBe('conta ····7777');
  });

  it('sem nome de banco, descreve só as contas', () => {
    expect(describeItemAccounts('', [account({})])).toBe('conta corrente ····1234');
    expect(describeItemAccounts('Banco Exemplo', [])).toBe('Banco Exemplo');
  });
});

describe('progresso do wizard', () => {
  const progress: WizardProgress = { ...EMPTY_PROGRESS, coupleId: 'couple-1', step: 3, banksConnected: true };

  it('a chave de armazenamento é por usuário e só tem caracteres aceitos pelo armazenamento seguro', () => {
    expect(progressStorageKey('user-1')).not.toBe(progressStorageKey('user-2'));
    expect(progressStorageKey('a/b c@d')).toMatch(/^[A-Za-z0-9._-]+$/);
  });

  it('grava só o passo e os campos não sensíveis: nunca o Client ID nem o Client Secret', () => {
    const withSecrets = {
      ...progress,
      clientId: 'fake-client-id-0000',
      clientSecret: 'fake-client-secret-0000',
      itemIds: ['a1b2c3d4-0000-4000-8000-000000000001'],
    } as WizardProgress;

    const raw = serializeProgress(withSecrets);

    expect(Object.keys(JSON.parse(raw)).sort()).toEqual(['banksConnected', 'connectionId', 'coupleId', 'step', 'version']);
    expect(raw).not.toContain('fake-client-secret-0000');
    expect(raw).not.toContain('fake-client-id-0000');
    expect(raw).not.toContain('a1b2c3d4');
  });

  it('o que foi gravado volta igual para o mesmo grupo', () => {
    expect(parseProgress(serializeProgress(progress), 'couple-1')).toEqual(progress);
    const withConnection = { ...progress, step: 4 as const, connectionId: 'conn-1' };
    expect(parseProgress(serializeProgress(withConnection), 'couple-1')).toEqual(withConnection);
  });

  it('progresso de outro grupo não vale: começa do zero no grupo atual', () => {
    expect(parseProgress(serializeProgress(progress), 'couple-2')).toEqual({ ...EMPTY_PROGRESS, coupleId: 'couple-2' });
  });

  it.each([null, undefined, '', 'não é json', '[]', '{}', '{"version":99,"step":3}', '{"version":1,"coupleId":"couple-1","step":9}'])(
    'conteúdo estranho (%p) vira "nunca começou"',
    (raw) => {
      expect(parseProgress(raw as string | null | undefined, 'couple-1')).toEqual({ ...EMPTY_PROGRESS, coupleId: 'couple-1' });
    },
  );
});

describe('em que passo o wizard abre', () => {
  const base = { ...EMPTY_PROGRESS, coupleId: 'couple-1' };

  it('quem nunca aceitou o aviso de privacidade começa no passo 1, não importa o progresso guardado', () => {
    expect(startingStep({ progress: { ...base, step: 3 }, consentAccepted: false, myConnection: null })).toBe(1);
  });

  it('quem já tem conexão ativa vai direto ao passo 4 (adicionar banco)', () => {
    expect(startingStep({ progress: base, consentAccepted: true, myConnection: connection({}) })).toBe(4);
    expect(startingStep({ progress: base, consentAccepted: false, myConnection: connection({ status: 'Error' }) })).toBe(4);
  });

  it('quem desconectou volta a informar as credenciais: no máximo o passo 3', () => {
    const disconnected = connection({ status: 'Disconnected', clientIdHint: null });
    expect(startingStep({ progress: { ...base, step: 4, connectionId: 'conn-1' }, consentAccepted: true, myConnection: disconnected })).toBe(3);
    expect(startingStep({ progress: { ...base, step: 2 }, consentAccepted: true, myConnection: disconnected })).toBe(2);
  });

  it('sem conexão, retoma do passo guardado; passo 4 sem conexão volta ao 3', () => {
    expect(startingStep({ progress: { ...base, step: 2 }, consentAccepted: true, myConnection: null })).toBe(2);
    expect(startingStep({ progress: { ...base, step: 4 }, consentAccepted: true, myConnection: null })).toBe(3);
    expect(startingStep({ progress: base, consentAccepted: true, myConnection: null })).toBe(2);
  });
});

describe('status do servidor', () => {
  const status = (over: Partial<OpenFinanceStatusResponse>): OpenFinanceStatusResponse => ({ available: true, connections: [], ...over });

  it('sem "available" a funcionalidade está indisponível (inclusive em servidor antigo, sem a rota)', () => {
    expect(isOpenFinanceAvailable(status({ available: false }))).toBe(false);
    expect(isOpenFinanceAvailable(undefined)).toBe(false);
    expect(isOpenFinanceAvailable({} as OpenFinanceStatusResponse)).toBe(false);
    expect(isOpenFinanceAvailable(status({}))).toBe(true);
    expect(UNAVAILABLE_TITLE).toBe('Indisponível neste servidor');
  });

  it('a minha conexão é a marcada como minha; a do parceiro não', () => {
    const mine = connection({ id: 'mine' });
    const partner = connection({ id: 'partner', isMine: false, userId: 'user-2', userName: 'Bruno' });
    expect(myConnectionOf(status({ connections: [partner, mine] }))?.id).toBe('mine');
    expect(myConnectionOf(status({ connections: [partner] }))).toBeNull();
    expect(myConnectionOf(undefined)).toBeNull();
  });

  it('o status da conexão aparece em português', () => {
    expect(connectionStatusLabel('Active')).toBe('Conectada');
    expect(connectionStatusLabel('Error')).toBe('Com erro');
    expect(connectionStatusLabel('Disconnected')).toBe('Desconectada');
    expect(connectionStatusLabel('SomethingNew')).toBe('Conectada');
  });

  it('conexão com erro: quem conectou lê o caminho que existe (desconectar e conectar de novo)', () => {
    expect(connectionErrorHelp(connection({ status: 'Error' }))).toBe(
      'Para voltar a funcionar, toque em "Desconectar" e depois em "Conectar de novo", com o Client ID e o Client Secret certos. Os bancos e as contas continuam aqui.',
    );
  });

  it('conexão com erro do parceiro: só quem conectou resolve', () => {
    expect(connectionErrorHelp(connection({ status: 'Error', isMine: false, userName: 'Bruno' }))).toBe(
      'Só Bruno pode resolver: desconectando e conectando de novo com as credenciais certas.',
    );
  });

  it('quem não conectou vê a conexão sem nenhum controle de edição', () => {
    const none = { syncSwitch: false, addBank: false, reconnect: false, disconnect: false };
    const partner = { isMine: false, userId: 'user-2', userName: 'Bruno' };
    expect(connectionControls(connection({ ...partner, status: 'Active' }), true)).toEqual(none);
    expect(connectionControls(connection({ ...partner, status: 'Error' }), true)).toEqual(none);
    expect(connectionControls(connection({ ...partner, status: 'Disconnected' }), true)).toEqual(none);
    expect(connectionControls(connection({ ...partner, status: 'Active' }), false)).toEqual(none);
  });

  it('quem conectou liga/desliga a sincronização, adiciona banco e desconecta', () => {
    const editing = { syncSwitch: true, addBank: true, reconnect: false, disconnect: true };
    expect(connectionControls(connection({ status: 'Active' }), true)).toEqual(editing);
    // "Com erro": o caminho é desconectar, então o botão continua lá.
    expect(connectionControls(connection({ status: 'Error' }), true)).toEqual(editing);
  });

  it('quem desconectou só tem "Conectar de novo" (não há o que desconectar nem onde adicionar banco)', () => {
    expect(connectionControls(connection({ status: 'Disconnected' }), true)).toEqual({
      syncSwitch: true,
      addBank: false,
      reconnect: true,
      disconnect: false,
    });
  });

  it('com o servidor indisponível, nem quem conectou tem botões (o interruptor aparece, desabilitado pela tela)', () => {
    const noButtons = { syncSwitch: true, addBank: false, reconnect: false, disconnect: false };
    expect(connectionControls(connection({ status: 'Active' }), false)).toEqual(noButtons);
    expect(connectionControls(connection({ status: 'Disconnected' }), false)).toEqual(noButtons);
  });

  it('conexão sem erro não mostra caminho de correção', () => {
    expect(connectionErrorHelp(connection({ status: 'Active' }))).toBeNull();
    expect(connectionErrorHelp(connection({ status: 'Disconnected' }))).toBeNull();
    // Mensagem antiga gravada numa conexão que já voltou a funcionar não traz a ajuda de volta.
    expect(connectionErrorHelp(connection({ status: 'Active', lastErrorMessage: 'antiga' }))).toBeNull();
  });
});

describe('passo 3: guardar a conexão quando o servidor diz que ela já existe', () => {
  const status = (connections: BankConnectionResponse[]): OpenFinanceStatusResponse => ({ available: true, connections });
  const partner = connection({ id: 'partner', isMine: false, userId: 'user-2', userName: 'Bruno' });

  it('segue para o passo 4 com a conexão que o servidor tem (criada em outro aparelho, ou resposta perdida)', async () => {
    const load = jest.fn(async () => status([partner, connection({ id: 'mine' })]));
    await expect(existingConnectionAfterCreateError('BANK_CONNECTION_ALREADY_EXISTS', load)).resolves.toBe('mine');
    expect(load).toHaveBeenCalledTimes(1);
  });

  it('qualquer outro erro continua sendo erro, e o status nem é buscado', async () => {
    const load = jest.fn(async () => status([connection({ id: 'mine' })]));
    await expect(existingConnectionAfterCreateError('PLUGGY_INVALID_CREDENTIALS', load)).resolves.toBeNull();
    await expect(existingConnectionAfterCreateError('BANK_CONNECTION_DISCONNECTED', load)).resolves.toBeNull();
    await expect(existingConnectionAfterCreateError(undefined, load)).resolves.toBeNull();
    expect(load).not.toHaveBeenCalled();
  });

  it('se o servidor não mostra uma conexão minha, o erro aparece (nunca segue com a do parceiro)', async () => {
    await expect(existingConnectionAfterCreateError('BANK_CONNECTION_ALREADY_EXISTS', async () => status([partner]))).resolves.toBeNull();
    await expect(existingConnectionAfterCreateError('BANK_CONNECTION_ALREADY_EXISTS', async () => status([]))).resolves.toBeNull();
    // A busca do status falhou (sem resposta).
    await expect(existingConnectionAfterCreateError('BANK_CONNECTION_ALREADY_EXISTS', async () => undefined)).resolves.toBeNull();
  });
});

describe('servidor antigo e bancos que precisam de atenção', () => {
  it('servidor sem a rota (404) é tratado como indisponível, não como erro', () => {
    const notFound = { response: { status: 404, data: { code: 'NOT_FOUND', message: 'Recurso não encontrado.' } } };
    expect(statusWhenRouteMissing(notFound)).toEqual({ available: false, connections: [] });
  });

  it('qualquer outro erro continua sendo erro (rede, 500, sem grupo)', () => {
    expect(statusWhenRouteMissing({ code: 'ERR_NETWORK', message: 'Network Error' })).toBeNull();
    expect(statusWhenRouteMissing({ response: { status: 500, data: {} } })).toBeNull();
    expect(statusWhenRouteMissing({ response: { status: 403, data: { code: 'COUPLE_REQUIRED', message: 'x' } } })).toBeNull();
    expect(statusWhenRouteMissing({ response: { status: 404, data: { code: 'COUPLE_NOT_FOUND', message: 'x' } } })).toBeNull();
    expect(statusWhenRouteMissing(null)).toBeNull();
  });

  it('banco com erro de login ou aguardando ação tem aviso; atualizado não tem', () => {
    expect(itemStatusNote('UPDATED')).toBeNull();
    expect(itemStatusNote('UPDATING')).toBeNull();
    expect(itemStatusNote('LOGIN_ERROR')).toMatch(/Meu Pluggy/);
    expect(itemStatusNote('WAITING_USER_INPUT')).toMatch(/Meu Pluggy/);
    expect(itemStatusNote('OUTDATED')).toMatch(/desatualizad/i);
    expect(itemStatusNote('SOMETHING_NEW')).toBeNull();
  });
});

describe('textos e links do wizard', () => {
  it('os links são os do Meu Pluggy e do Dashboard, em https', () => {
    expect(MEU_PLUGGY_URL).toBe('https://meu.pluggy.ai');
    expect(DASHBOARD_URL).toBe('https://dashboard.pluggy.ai');
  });

  it('passo 2: criar conta, "Conectar conta", escolher o banco e autorizar no app do banco', () => {
    const text = MEU_PLUGGY_STEPS.join('\n');
    expect(MEU_PLUGGY_STEPS).toHaveLength(4);
    expect(text).toMatch(/Crie a sua conta/);
    expect(text).toContain('"Conectar conta"');
    expect(text).toMatch(/Escolha o banco/);
    expect(text).toMatch(/Autorize no app do banco/);
  });

  it('passo 3: mesmo e-mail, criar aplicação, aba "Aplicação", copiar Client ID e Client Secret', () => {
    const text = DASHBOARD_STEPS.join('\n');
    expect(text).toMatch(/mesmo e-mail/);
    expect(text).toMatch(/Crie uma aplicação/);
    expect(text).toContain('"Aplicação"');
    expect(text).toContain('Client ID');
    expect(text).toContain('Client Secret');
  });

  it('passo 4: Conectores, MeuPluggy, Demo, Conectar conta, três pontos e "Copiar Item ID"', () => {
    const text = ITEM_STEPS.join('\n');
    for (const piece of ['"Conectores"', '"MeuPluggy"', '"Ir para Demo"', '"Conectar conta"', 'três pontos', '"Copiar Item ID"']) {
      expect(text).toContain(piece);
    }
    expect(text).toMatch(/conta do Meu Pluggy/);
  });

  it('o wizard termina avisando que a sincronização vem na próxima atualização', () => {
    expect(WIZARD_DONE_MESSAGE).toBe('Conexão pronta. A sincronização chega na próxima atualização do app.');
  });
});

describe('saldo e limite na tela de gestão, com a data em que foram lidos', () => {
  // Formatador falso: o teste não depende do Intl do aparelho.
  const money = (value: number, currency: string) => `${currency} ${value.toFixed(2).replace('.', ',')}`;

  it('conta: "Saldo", com a data da leitura em horário de Brasília', () => {
    expect(accountBalanceText(account({ balance: 10, balanceAtUtc: '2026-10-07T12:00:00Z' }), money)).toBe(
      'Saldo: BRL 10,00 · lido em 07/10/2026',
    );
    // 01:00 UTC ainda é o dia anterior em Brasília.
    expect(accountBalanceText(account({ balanceAtUtc: '2026-10-07T01:00:00Z' }), money)).toBe('Saldo: BRL 10,00 · lido em 06/10/2026');
  });

  it('cartão: rótulo neutro "Saldo do cartão" (não afirma que é a fatura), limite disponível e data', () => {
    const text = accountBalanceText(
      account({ type: 'CREDIT', subtype: 'CREDIT_CARD', balance: 250, availableCreditLimit: 750, balanceAtUtc: '2026-10-07T12:00:00Z' }),
      money,
    );
    expect(text).toBe('Saldo do cartão: BRL 250,00 · Limite disponível: BRL 750,00 · lido em 07/10/2026');
    expect(text).not.toContain('Fatura');
  });

  it('sem data (vazia ou inválida): o texto vem sem a parte da data', () => {
    expect(accountBalanceText(account({ balanceAtUtc: '' }), money)).toBe('Saldo: BRL 10,00');
    expect(accountBalanceText(account({ balanceAtUtc: 'ontem' }), money)).toBe('Saldo: BRL 10,00');
  });

  it('limite disponível zero ainda aparece; sem limite, não', () => {
    expect(accountBalanceText(account({ availableCreditLimit: 0 }), money)).toContain('Limite disponível: BRL 0,00');
    expect(accountBalanceText(account({ availableCreditLimit: null }), money)).not.toContain('Limite');
  });
});
