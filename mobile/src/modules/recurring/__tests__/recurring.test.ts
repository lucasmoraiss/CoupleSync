// Issue #39: as regras de apresentação de "Assinaturas e recorrências" e o que a tela e o cartão do Painel têm de
// ter. O Jest daqui não renderiza React Native: a segunda parte lê o código das telas (como os testes de navegação).
import * as fs from 'fs';
import * as path from 'path';
import { PARENT_ROUTE } from '@/navigation/routes';
import type { RecurringItemResponse, RecurringListResponse } from '@/types/api';
import {
  RECURRING_CARD_EMPTY_LABEL,
  RECURRING_CARD_EMPTY_TEXT,
  RECURRING_CARD_ERROR_LABEL,
  RECURRING_CARD_ERROR_TEXT,
  RECURRING_EMPTY_TEXT,
  RECURRING_ENTRY_LABEL,
  RECURRING_NOT_LISTED_NOTE,
  RECURRING_QUERY_KEY,
  RECURRING_STALE_TEXT,
  RECURRING_TOTALS_NOTE,
  amountText,
  dateLabel,
  isRecurringEmpty,
  itemActions,
  itemBadges,
  itemDetail,
  itemLabel,
  monthLabel,
  overrideDoneMessage,
  recurringCardLabel,
  recurringCardText,
  recurringSections,
} from '../recurring';

const MOBILE_DIR = path.resolve(__dirname, '../../../..');
const read = (relative: string) => fs.readFileSync(path.join(MOBILE_DIR, relative), 'utf8');
/** O Intl usa espaço não separável depois de "R$": os testes comparam com espaço comum. */
const plain = (text: string) => text.replace(/ /g, ' ');

function item(overrides: Partial<RecurringItemResponse> = {}): RecurringItemResponse {
  return {
    id: 'a',
    name: 'Streaming Exemplo',
    kind: 'Subscription',
    variableAmount: false,
    cadence: 'Monthly',
    amount: 39.9,
    lastAmount: 39.9,
    previousAmount: null,
    annualCost: 478.8,
    occurrences: 3,
    firstSeen: '2026-08-05',
    lastSeen: '2026-10-05',
    nextExpected: '2026-11-05',
    status: 'Active',
    flags: [],
    confidence: 'High',
    category: 'LAZER',
    person: null,
    installment: null,
    override: null,
    ...overrides,
  };
}

function list(overrides: Partial<RecurringListResponse> = {}): RecurringListResponse {
  return {
    monthlyTotal: 0,
    annualTotal: 0,
    detectedAtUtc: '2026-10-08T15:00:00Z',
    subscriptions: [],
    fixedBills: [],
    installments: [],
    habits: [],
    hidden: [],
    ...overrides,
  };
}

describe('cartão do Painel', () => {
  it('mostra "Recorrências: R$ X/mês" com o total mensal do servidor', () => {
    expect(plain(recurringCardText({ monthlyTotal: 369.9 }))).toBe('Recorrências: R$ 369,90/mês');
    expect(plain(recurringCardText({ monthlyTotal: 0 }))).toBe('Recorrências: R$ 0,00/mês');
  });

  it('o rótulo acessível diz o valor por extenso e para onde o toque leva', () => {
    expect(recurringCardLabel({ monthlyTotal: 39.9 })).toBe('Recorrências: 39 reais e 90 centavos por mês. Abrir assinaturas e recorrências');
  });
});

describe('seções da tela', () => {
  it('saem na ordem fixa e só as que têm item', () => {
    const data = list({
      hidden: [item({ id: 'h', override: 'NotRecurring' })],
      habits: [item({ id: 'p', kind: 'Habit', cadence: 'Irregular' })],
      subscriptions: [item()],
    });
    expect(recurringSections(data).map((s) => s.title)).toEqual(['Assinaturas', 'Pequenos gastos frequentes', 'Ocultas']);
  });

  it('todas as cinco, com os títulos da tela', () => {
    const data = list({
      subscriptions: [item()],
      fixedBills: [item({ id: 'b', kind: 'FixedBill' })],
      installments: [item({ id: 'c', kind: 'Installment' })],
      habits: [item({ id: 'd', kind: 'Habit' })],
      hidden: [item({ id: 'e', override: 'Cancelled' })],
    });
    expect(recurringSections(data).map((s) => s.title)).toEqual([
      'Assinaturas',
      'Contas fixas',
      'Parcelamentos',
      'Pequenos gastos frequentes',
      'Ocultas',
    ]);
  });

  it('lista sem nenhum item é o estado vazio, com o texto pedido', () => {
    expect(isRecurringEmpty(list())).toBe(true);
    expect(isRecurringEmpty(list({ hidden: [item()] }))).toBe(false);
    expect(RECURRING_EMPTY_TEXT).toBe('Ainda não há histórico suficiente: precisamos de 3 cobranças parecidas');
  });

  it('uma resposta sem alguma seção (servidor mais antigo) não quebra a tela', () => {
    const partial = { monthlyTotal: 0, annualTotal: 0, detectedAtUtc: '', subscriptions: [item()] } as unknown as RecurringListResponse;
    expect(recurringSections(partial).map((s) => s.key)).toEqual(['subscriptions']);
  });
});

describe('marcas de um item', () => {
  it('assinatura: "talvez esquecida — vocês ainda usam?", "ficou mais cara" e "nova"', () => {
    expect(itemBadges(item({ flags: ['Forgotten'] }))).toEqual(['talvez esquecida — vocês ainda usam?']);
    expect(itemBadges(item({ flags: ['PriceIncrease'] }))).toEqual(['ficou mais cara']);
    expect(itemBadges(item({ flags: ['New'] }))).toEqual(['nova']);
    expect(itemBadges(item({ flags: ['Forgotten', 'PriceIncrease', 'New'] }))).toEqual([
      'talvez esquecida — vocês ainda usam?',
      'ficou mais cara',
      'nova',
    ]);
  });

  it('conta fixa de valor variável: "valor variável"', () => {
    expect(itemBadges(item({ kind: 'FixedBill', variableAmount: true }))).toEqual(['valor variável']);
  });

  it('parcela vista uma vez só: "parcela provável"', () => {
    expect(itemBadges(item({ kind: 'Installment', confidence: 'Low' }))).toEqual(['parcela provável']);
    expect(itemBadges(item({ kind: 'Installment', confidence: 'High' }))).toEqual([]);
  });

  it('o que a pessoa corrigiu e o que parou aparecem escritos', () => {
    expect(itemBadges(item({ override: 'NotRecurring' }))).toEqual(['marcada como não recorrente']);
    expect(itemBadges(item({ override: 'Cancelled' }))).toEqual(['marcada como cancelada']);
    expect(itemBadges(item({ override: 'Cancelled', flags: ['ChargedAfterCancel'] }))).toEqual(['cobrou de novo depois de cancelada']);
    expect(itemBadges(item({ status: 'SuspectedDormant' }))).toEqual(['sem cobrança recente']);
    expect(itemBadges(item({ status: 'Stopped' }))).toEqual(['parou de ser cobrada']);
    // Cobrou de novo depois de cancelada: as duas marcas juntas se contradiziam.
    expect(itemBadges(item({ override: 'Cancelled', status: 'Stopped', flags: ['ChargedAfterCancel'] }))).toEqual([
      'cobrou de novo depois de cancelada',
    ]);
  });

  it('item sem nada de especial não tem marca (nem quebra sem o campo flags)', () => {
    expect(itemBadges(item())).toEqual([]);
    expect(itemBadges({ ...item(), flags: undefined } as unknown as RecurringItemResponse)).toEqual([]);
  });
});

describe('valor e detalhe de um item', () => {
  it('o valor diz o ritmo', () => {
    expect(plain(amountText(item()))).toBe('R$ 39,90 por mês');
    expect(plain(amountText(item({ cadence: 'Weekly', amount: 80 })))).toBe('R$ 80,00 por semana');
    expect(plain(amountText(item({ cadence: 'Yearly', amount: 1200 })))).toBe('R$ 1.200,00 por ano');
    expect(plain(amountText(item({ cadence: 'Irregular', amount: 12.5 })))).toBe('R$ 12,50 por compra');
  });

  it('parcelamento: n/N, falta R$ e termina em', () => {
    const installment = item({
      kind: 'Installment',
      amount: 150,
      installment: { number: 3, total: 10, remainingAmount: 1050, endMonth: '2027-05' },
    });
    expect(plain(itemDetail(installment))).toBe('Parcela 3 de 10 · falta R$ 1.050,00 · termina em mai/2027');
  });

  it('pequeno gasto frequente: quantas compras e o custo em 12 meses', () => {
    const habit = item({ kind: 'Habit', cadence: 'Irregular', amount: 12.5, occurrences: 14, annualCost: 2100, nextExpected: null });
    expect(plain(itemDetail(habit))).toBe('14 compras em 30 dias · R$ 2.100,00 em 12 meses nesse ritmo');
  });

  it('assinatura: custo anual, valor de antes do reajuste, próxima data e de quem é', () => {
    expect(plain(itemDetail(item()))).toBe('R$ 478,80 por ano · próxima por volta de 05/11/2026');
    expect(plain(itemDetail(item({ previousAmount: 34.9, person: { userId: 'u', name: 'Ana Exemplo' } })))).toBe(
      'R$ 478,80 por ano · antes R$ 34,90 · próxima por volta de 05/11/2026 · Ana Exemplo',
    );
  });

  it('conta de valor variável mostra a última cobrança; item parado não promete próxima data', () => {
    const bill = item({ kind: 'FixedBill', variableAmount: true, amount: 180, lastAmount: 150, annualCost: 2160 });
    expect(plain(itemDetail(bill))).toBe('R$ 2.160,00 por ano · última R$ 150,00 · próxima por volta de 05/11/2026');
    expect(plain(itemDetail(item({ status: 'Stopped' })))).toBe('R$ 478,80 por ano');
  });

  it('o rótulo acessível traz nome, valor por extenso, marcas e o que o toque faz', () => {
    expect(itemLabel(item())).toBe('Streaming Exemplo, 39 reais e 90 centavos por mês. Ver cobranças e opções');
    expect(itemLabel(item({ flags: ['Forgotten'] }))).toBe(
      'Streaming Exemplo, 39 reais e 90 centavos por mês. talvez esquecida — vocês ainda usam?. Ver cobranças e opções',
    );
  });

  it('datas e meses', () => {
    expect(monthLabel('2027-05')).toBe('mai/2027');
    expect(monthLabel('2026-12')).toBe('dez/2026');
    expect(monthLabel('x')).toBe('x');
    expect(dateLabel('2026-11-05')).toBe('05/11/2026');
    expect(dateLabel('x')).toBe('x');
  });
});

describe('correções que cabem em cada item', () => {
  const labels = (i: RecurringItemResponse) => itemActions(i).map((a) => a.label);

  it('assinatura: "Não é recorrente", "Cancelei" e "É conta fixa"', () => {
    expect(labels(item())).toEqual(['Não é recorrente', 'Cancelei', 'É conta fixa']);
  });

  it('conta fixa: "Não é recorrente", "Cancelei" e "É assinatura"', () => {
    expect(labels(item({ kind: 'FixedBill' }))).toEqual(['Não é recorrente', 'Cancelei', 'É assinatura']);
  });

  it('pequeno gasto: pode virar assinatura ou conta fixa; não há o que cancelar', () => {
    expect(labels(item({ kind: 'Habit' }))).toEqual(['Não é recorrente', 'É assinatura', 'É conta fixa']);
  });

  it('parcelamento: só "Não é recorrente"', () => {
    expect(labels(item({ kind: 'Installment' }))).toEqual(['Não é recorrente']);
  });

  it('com uma correção feita, a primeira ação é desfazê-la (manda null) e ela não se repete', () => {
    const corrected = itemActions(item({ override: 'NotRecurring' }));
    expect(corrected[0]).toEqual({ override: null, label: 'Desfazer a correção' });
    expect(corrected.map((a) => a.override)).toEqual([null, 'Cancelled', 'FixedBill']);
    expect(itemActions(item({ kind: 'FixedBill', override: 'FixedBill' })).map((a) => a.override)).toEqual([null, 'NotRecurring', 'Cancelled', 'Subscription']);
  });

  it('cada correção tem o seu aviso', () => {
    expect(overrideDoneMessage('Streaming Exemplo', 'NotRecurring')).toBe('Streaming Exemplo foi para Ocultas: não é recorrente.');
    expect(overrideDoneMessage('Streaming Exemplo', 'Cancelled')).toContain('Se cobrar de novo');
    expect(overrideDoneMessage('Streaming Exemplo', 'Subscription')).toBe('Streaming Exemplo agora é assinatura.');
    expect(overrideDoneMessage('Streaming Exemplo', 'FixedBill')).toBe('Streaming Exemplo agora é conta fixa.');
    expect(overrideDoneMessage('Streaming Exemplo', null)).toBe('Correção desfeita em Streaming Exemplo.');
  });
});

describe('a tela, o cartão e as rotas (código lido do arquivo)', () => {
  const screen = read('app/(main)/recurring/index.tsx');
  const dashboard = read('app/(main)/index.tsx');
  const layout = read('app/(main)/_layout.tsx');
  const api = read('src/services/apiClient.ts');

  it('a tela é aba oculta, volta para o Painel e é remontada a cada visita', () => {
    expect(layout).toContain('<Tabs.Screen name="recurring/index" options={{ href: null }} />');
    expect(PARENT_ROUTE['recurring/index']).toBe('/');
    expect(screen).toMatch(/export default resetOnFocus\(RecurringScreen\)/);
    expect(screen).toMatch(/goToParent\('recurring\/index'\)/);
    expect(screen).toContain('accessibilityLabel="Voltar para o Painel"');
  });

  it('tem os três estados (carregando, erro com nova tentativa, vazio) e o título', () => {
    expect(screen).toMatch(/\{isLoading && <LoadingState \/>\}/);
    expect(screen).toMatch(/\{isError && !isLoading && !data && \(\s*<ErrorState[\s\S]{0,200}onRetry=/);
    expect(screen).toMatch(/isRecurringEmpty\(data\) && \([\s\S]{0,300}\{RECURRING_EMPTY_TEXT\}/);
    expect(screen).toMatch(/accessibilityRole="header">\{RECURRING_TITLE\}/);
  });

  it('mostra o total mensal e o anual que vêm do servidor', () => {
    expect(screen).toMatch(/\{formatBRL\(data\.monthlyTotal\)\}/);
    expect(screen).toMatch(/\{formatBRL\(data\.annualTotal\)\}/);
  });

  it('cada linha usa as regras testadas e tem rótulo acessível; o toque abre as cobranças e as correções', () => {
    expect(screen).toMatch(/recurringSections\(data\)/);
    expect(screen).toMatch(/accessibilityLabel=\{itemLabel\(item\)\}/);
    expect(screen).toMatch(/itemBadges\(item\)/);
    expect(screen).toMatch(/\{itemDetail\(item\)\}/);
    expect(screen).toMatch(/\{open && \(\s*<View style=\{styles\.itemBody\}>\s*<Charges id=\{item\.id\} \/>/);
    expect(screen).toMatch(/itemActions\(item\)\.map\(\(action\) => \(\s*<TouchableOpacity[\s\S]{0,400}accessibilityLabel=\{action\.label\}/);
  });

  it('a correção vai ao servidor, fica presa à sessão e atualiza a lista e o cartão', () => {
    expect(screen).toMatch(/const epoch = getSessionEpoch\(\);/);
    expect(screen).toMatch(/await recurringApiClient\.setOverride\(item\.id, action\.override\);\s*\/\/[^\n]*\n\s*if \(getSessionEpoch\(\) !== epoch\) return;/);
    expect(screen).toMatch(/invalidateQueries\(\{ queryKey: RECURRING_QUERY_KEY \}\)/);
    expect(RECURRING_QUERY_KEY).toEqual(['recurring']);
  });

  it('a tela não tem campo de texto nem formata dinheiro por conta própria', () => {
    expect(screen).not.toMatch(/<TextInput\b/);
    expect(screen).not.toMatch(/Intl\.NumberFormat/);
  });

  it('o cartão do Painel nunca some em silêncio: total, vazio que explica, erro com "tentar de novo"', () => {
    // Com a resposta em mãos (mesmo que a última consulta tenha falhado): o total, ou o vazio que diz o que vai aparecer.
    expect(dashboard).toMatch(
      /\{recurring\.data \? \(\s*<TouchableOpacity[\s\S]{0,200}router\.push\('\/\(main\)\/recurring' as any\)[\s\S]{0,200}accessibilityLabel=\{isRecurringEmpty\(recurring\.data\) \? RECURRING_CARD_EMPTY_LABEL : recurringCardLabel\(recurring\.data\)\}/,
    );
    expect(dashboard).toMatch(/\{isRecurringEmpty\(recurring\.data\) \? RECURRING_CARD_EMPTY_TEXT : recurringCardText\(recurring\.data\)\}/);
    // Em erro, sem resposta: o estado de erro com o botão que consulta de novo.
    expect(dashboard).toMatch(
      /\) : recurring\.isError \? \(\s*<TouchableOpacity[\s\S]{0,200}onPress=\{\(\) => void recurring\.refetch\(\)\}[\s\S]{0,200}accessibilityLabel=\{RECURRING_CARD_ERROR_LABEL\}[\s\S]{0,300}\{RECURRING_CARD_ERROR_TEXT\}[\s\S]{0,200}Tentar de novo/,
    );
    // Carregando: o cartão já ocupa o lugar dele.
    expect(dashboard).toMatch(/\) : \(\s*<View style=\{styles\.aiCard\}[\s\S]{0,300}Carregando as recorrências/);
    expect(dashboard).not.toMatch(/recurring\.data && !recurring\.isError/);
    expect(dashboard.match(/\(main\)\/recurring/g)).toHaveLength(1);
    // O Painel fica montado: pergunta de novo a cada volta e ao puxar para atualizar.
    expect(dashboard).toMatch(/useOnRefocus\(\(\) => void recurring\.refetch\(\{ cancelRefetch: false \}\)\);/);
    expect(dashboard).toMatch(/const recurringRefresh = recurring\.refetch\(\);[\s\S]{0,200}await recurringRefresh;/);
  });

  it('os textos do cartão dizem a verdade nos dois estados novos', () => {
    expect(RECURRING_CARD_EMPTY_TEXT).toBe('Assinaturas e contas fixas aparecem aqui depois de 3 cobranças parecidas');
    expect(RECURRING_CARD_EMPTY_LABEL).toBe(
      'Assinaturas e contas fixas: ainda não há histórico suficiente. Elas aparecem aqui depois de 3 cobranças parecidas. Abrir assinaturas e recorrências',
    );
    expect(RECURRING_CARD_ERROR_TEXT).toBe('Não foi possível carregar as recorrências');
    expect(RECURRING_CARD_ERROR_LABEL).toBe('Não foi possível carregar as recorrências. Tentar de novo');
  });

  it('há uma segunda entrada, fixa, em Configurações: "Assinaturas e contas fixas"', () => {
    const settings = read('app/(main)/settings/index.tsx');
    expect(RECURRING_ENTRY_LABEL).toBe('Assinaturas e contas fixas');
    expect(settings).toMatch(
      /<TouchableOpacity\s+style=\{styles\.menuItem\}\s+onPress=\{\(\) => router\.push\('\/\(main\)\/recurring' as any\)\}\s+accessibilityLabel="Abrir assinaturas e contas fixas: o que o grupo paga todo mês"\s+accessibilityRole="button"\s*>\s*<Text style=\{styles\.menuText\}>\{RECURRING_ENTRY_LABEL\}<\/Text>/,
    );
  });

  it('a tela avisa quando mostra a lista antiga, e o indicador de puxar só aparece quando a pessoa puxa', () => {
    expect(RECURRING_STALE_TEXT).toBe('Não foi possível atualizar agora. Esta é a última lista carregada.');
    expect(screen).toMatch(/\{isError && !!data && <Text style=\{styles\.stale\} accessibilityRole="alert">\{RECURRING_STALE_TEXT\}<\/Text>\}/);
    expect(screen).toMatch(/refreshing=\{pulling\}/);
    expect(screen).not.toMatch(/refreshing=\{isRefetching\}/);
  });

  it('as notas dizem o que entra no total e o que nunca aparece na lista', () => {
    expect(RECURRING_TOTALS_NOTE).toBe(
      'Soma de assinaturas, contas fixas e parcelas confirmadas. Cobrança anual entra no mês como 1/12; de um parcelamento, "Em 12 meses" é só o que falta pagar. Pequenos gastos frequentes, parcelas prováveis e itens ocultos não entram.',
    );
    expect(RECURRING_NOT_LISTED_NOTE).toBe('Pix e transferências para pessoas não aparecem aqui, mesmo que se repitam.');
    expect(screen.match(/\{RECURRING_NOT_LISTED_NOTE\}/g)).toHaveLength(2);
  });

  it('as três rotas da API são as do contrato', () => {
    expect(api).toContain("axiosInstance.get<RecurringListResponse>('/api/v1/ai/recurring')");
    expect(api).toContain('axiosInstance.get<RecurringChargesResponse>(`/api/v1/ai/recurring/${id}/transactions`)');
    expect(api).toContain('axiosInstance.patch<RecurringItemResponse>(`/api/v1/ai/recurring/${id}`, { override })');
  });

  it('a lista vive só no cache de consultas (limpo ao sair da conta e ao trocar de grupo)', () => {
    const hook = read('src/modules/recurring/useRecurring.ts');
    expect(hook).toMatch(/queryKey: RECURRING_QUERY_KEY,/);
    expect(hook).toMatch(/enabled: signedIn,/);
    const moduleSource = read('src/modules/recurring/recurring.ts');
    expect(`${hook}${moduleSource}${screen}`).not.toMatch(/AsyncStorage|SecureStore|zustand/);
  });
});
