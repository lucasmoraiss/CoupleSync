// Open Finance (issue #27): regras puras da tela "Contas" e do cartão "Faturas a vencer" do Painel. Tudo aqui é
// inventado ("Banco Exemplo", ids falsos); nenhum dado bancário real.
import {
  accountKindLabel,
  billDatesText,
  cardLimitText,
  peopleSections,
  upcomingBillsCard,
  updatedAgoText,
} from '../accounts';
import type {
  BankAccountsOverviewResponse,
  BankAccountsPersonResponse,
  BankBalanceAccountResponse,
  BankCardResponse,
  CardBillResponse,
  UpcomingBillResponse,
  UpcomingBillsResponse,
} from '@/types/api';

const account = (over: Partial<BankBalanceAccountResponse> = {}): BankBalanceAccountResponse => ({
  id: 'a-1',
  bankName: 'Banco Exemplo',
  name: 'Conta Corrente',
  type: 'BANK',
  subtype: 'CHECKING_ACCOUNT',
  numberMasked: '1234',
  currency: 'BRL',
  balance: 1234.56,
  balanceAtUtc: '2026-10-08T12:00:00Z',
  ...over,
});

const bill = (over: Partial<CardBillResponse> = {}): CardBillResponse => ({
  id: 'b-1',
  dueDate: '2026-10-27',
  closingDate: '2026-10-20',
  totalAmount: 600,
  currency: 'BRL',
  minimumPayment: 60,
  isClosed: true,
  ...over,
});

const card = (over: Partial<BankCardResponse> = {}): BankCardResponse => ({
  id: 'c-1',
  bankName: 'Banco Exemplo',
  name: 'Cartão Exemplo Platinum',
  brand: 'MASTERCARD',
  numberMasked: '5678',
  currency: 'BRL',
  balance: 987.65,
  balanceAtUtc: '2026-10-08T12:00:00Z',
  creditLimit: 5000,
  availableCreditLimit: 4012.35,
  closeDate: '2026-10-20',
  dueDate: '2026-10-27',
  currentBill: bill(),
  nextBills: [],
  previousBills: [],
  ...over,
});

/** O Intl separa o símbolo do número com um espaço que não quebra; os textos esperados aqui usam o comum. */
const plain = (text: string | null): string | null => (text === null ? null : text.replace(/ /g, ' '));

const person = (over: Partial<BankAccountsPersonResponse> = {}): BankAccountsPersonResponse => ({
  userId: 'u-ana',
  userName: 'Ana Exemplo',
  isMe: false,
  accounts: [account()],
  cards: [],
  ...over,
});

describe('peopleSections: as contas agrupadas por pessoa', () => {
  it('põe quem está logado primeiro, como "Você", e os outros por nome', () => {
    const response: BankAccountsOverviewResponse = {
      people: [
        person({ userId: 'u-carla', userName: 'Carla Modelo' }),
        person({ userId: 'u-bruno', userName: 'bruno Modelo' }),
        person({ userId: 'u-ana', userName: 'Ana Exemplo', isMe: true }),
      ],
    };

    const sections = peopleSections(response);

    expect(sections.map((s) => s.title)).toEqual(['Você', 'bruno Modelo', 'Carla Modelo']);
    expect(sections.map((s) => s.userId)).toEqual(['u-ana', 'u-bruno', 'u-carla']);
    expect(sections[0].isMe).toBe(true);
  });

  it('cada pessoa fica com as contas e os cartões dela, e só com eles', () => {
    const sections = peopleSections({
      people: [
        person({ userId: 'u-ana', isMe: true, accounts: [account({ id: 'a-ana' })], cards: [card({ id: 'c-ana' })] }),
        person({ userId: 'u-bruno', userName: 'Bruno Modelo', accounts: [account({ id: 'a-bruno-1' }), account({ id: 'a-bruno-2' })], cards: [] }),
      ],
    });

    expect(sections[0].accounts.map((a) => a.id)).toEqual(['a-ana']);
    expect(sections[0].cards.map((c) => c.id)).toEqual(['c-ana']);
    expect(sections[1].accounts.map((a) => a.id)).toEqual(['a-bruno-1', 'a-bruno-2']);
    expect(sections[1].cards).toEqual([]);
  });

  it('quem não tem conta nem cartão não vira seção', () => {
    const sections = peopleSections({
      people: [person({ userId: 'u-vazio', userName: 'Sem nada', accounts: [], cards: [] }), person({ userId: 'u-ana', isMe: true })],
    });

    expect(sections.map((s) => s.userId)).toEqual(['u-ana']);
  });

  it('soma por pessoa só os saldos em reais; conta em outra moeda aparece e não entra na soma', () => {
    const [section] = peopleSections({
      people: [
        person({
          isMe: true,
          accounts: [
            account({ id: 'a-1', balance: 100.1 }),
            account({ id: 'a-2', balance: 200.2 }),
            account({ id: 'a-usd', balance: 9999, currency: 'USD' }),
          ],
          // O saldo do cartão não é dinheiro em conta: nunca entra.
          cards: [card({ balance: 5000 })],
        }),
      ],
    });

    expect(section.balanceBrl).toBeCloseTo(300.3, 2);
    expect(section.hasOtherCurrency).toBe(true);
    expect(section.accounts).toHaveLength(3);
  });

  it('sem conta em reais a soma é zero e não diz que há outra moeda à toa', () => {
    const [onlyCards] = peopleSections({ people: [person({ isMe: true, accounts: [], cards: [card()] })] });
    expect(onlyCards.balanceBrl).toBe(0);
    expect(onlyCards.hasOtherCurrency).toBe(false);
  });

  it('resposta vazia, ausente ou de servidor antigo: nenhuma seção', () => {
    expect(peopleSections({ people: [] })).toEqual([]);
    expect(peopleSections(undefined)).toEqual([]);
    expect(peopleSections(null)).toEqual([]);
    expect(peopleSections({} as BankAccountsOverviewResponse)).toEqual([]);
  });
});

describe('updatedAgoText: "atualizado há"', () => {
  const now = Date.parse('2026-10-08T15:00:00Z');
  const ago = (ms: number) => new Date(now - ms).toISOString();
  const minute = 60 * 1000;
  const hour = 60 * minute;
  const day = 24 * hour;

  it('menos de um minuto é "agora"', () => {
    expect(updatedAgoText(ago(0), now)).toBe('atualizado agora');
    expect(updatedAgoText(ago(59 * 1000), now)).toBe('atualizado agora');
  });

  it('minutos, horas e dias, sempre arredondando para baixo', () => {
    expect(updatedAgoText(ago(minute), now)).toBe('atualizado há 1 min');
    expect(updatedAgoText(ago(59 * minute + 59 * 1000), now)).toBe('atualizado há 59 min');
    expect(updatedAgoText(ago(hour), now)).toBe('atualizado há 1 h');
    expect(updatedAgoText(ago(23 * hour + 59 * minute), now)).toBe('atualizado há 23 h');
    expect(updatedAgoText(ago(day), now)).toBe('atualizado há 1 dia');
    expect(updatedAgoText(ago(2 * day + 5 * hour), now)).toBe('atualizado há 2 dias');
    expect(updatedAgoText(ago(40 * day), now)).toBe('atualizado há 40 dias');
  });

  it('relógio do aparelho atrasado (instante no futuro) é "agora", nunca um tempo negativo', () => {
    expect(updatedAgoText(new Date(now + 5 * minute).toISOString(), now)).toBe('atualizado agora');
  });

  it('sem instante ou com texto que não é data: nada a dizer', () => {
    expect(updatedAgoText(null, now)).toBeNull();
    expect(updatedAgoText(undefined, now)).toBeNull();
    expect(updatedAgoText('', now)).toBeNull();
    expect(updatedAgoText('ontem', now)).toBeNull();
  });
});

describe('textos da conta e do cartão', () => {
  it('accountKindLabel', () => {
    expect(accountKindLabel(account({ subtype: 'CHECKING_ACCOUNT' }))).toBe('Conta corrente');
    expect(accountKindLabel(account({ subtype: 'SAVINGS_ACCOUNT' }))).toBe('Poupança');
    expect(accountKindLabel(account({ subtype: null }))).toBe('Conta');
    expect(accountKindLabel(account({ subtype: 'OUTRA_COISA' }))).toBe('Conta');
  });

  it('billDatesText: vencimento sempre; fechamento quando o banco diz, no passado ou no futuro', () => {
    expect(billDatesText(bill({ dueDate: '2026-10-27', closingDate: '2026-10-20', isClosed: true }))).toBe('Vence em 27/10/2026 · fechou em 20/10/2026');
    expect(billDatesText(bill({ dueDate: '2026-11-27', closingDate: '2026-11-20', isClosed: false }))).toBe('Vence em 27/11/2026 · fecha em 20/11/2026');
    expect(billDatesText(bill({ dueDate: '2026-10-27', closingDate: null, isClosed: true }))).toBe('Vence em 27/10/2026 · fechada');
    expect(billDatesText(bill({ dueDate: '2026-10-27', closingDate: null, isClosed: false }))).toBe('Vence em 27/10/2026');
  });

  it('cardLimitText: na moeda do cartão; sem limite informado, nada', () => {
    expect(plain(cardLimitText(card({ creditLimit: 5000, availableCreditLimit: 4012.35 })))).toBe('Limite disponível R$ 4.012,35 de R$ 5.000,00');
    expect(plain(cardLimitText(card({ creditLimit: 5000, availableCreditLimit: null })))).toBe('Limite R$ 5.000,00');
    expect(plain(cardLimitText(card({ creditLimit: null, availableCreditLimit: 100 })))).toBe('Limite disponível R$ 100,00');
    expect(cardLimitText(card({ creditLimit: null, availableCreditLimit: null }))).toBeNull();
    expect(plain(cardLimitText(card({ currency: 'USD', creditLimit: 1000, availableCreditLimit: 400 })))).toBe('Limite disponível US$ 400,00 de US$ 1.000,00');
  });
});

describe('upcomingBillsCard: "Faturas a vencer nos próximos 30 dias" do Painel', () => {
  const due = (over: Partial<UpcomingBillResponse> = {}): UpcomingBillResponse => ({
    id: 'b-1',
    accountId: 'c-1',
    cardName: 'Cartão Exemplo',
    bankName: 'Banco Exemplo',
    userId: 'u-ana',
    userName: 'Ana Exemplo',
    dueDate: '2026-10-27',
    totalAmount: 100,
    currency: 'BRL',
    isClosed: true,
    ...over,
  });
  const upcoming = (over: Partial<UpcomingBillsResponse> = {}): UpcomingBillsResponse => ({
    totalBrl: 300.3,
    fromDay: '2026-10-08',
    toDay: '2026-11-07',
    bills: [due({ id: 'b-1', totalAmount: 100.1 }), due({ id: 'b-2', totalAmount: 200.2, dueDate: '2026-11-05' })],
    ...over,
  });

  it('sem conexão (campo ausente ou null) ou em servidor antigo: o cartão não aparece', () => {
    expect(upcomingBillsCard(undefined)).toBeNull();
    expect(upcomingBillsCard(null)).toBeNull();
  });

  it('mostra o total em reais que o servidor somou e quantas faturas são', () => {
    const shown = upcomingBillsCard(upcoming())!;
    expect(shown.totalBrl).toBe(300.3);
    expect(plain(shown.totalText)).toBe('R$ 300,30');
    expect(shown.countText).toBe('2 faturas · a primeira vence em 27/10/2026');
    expect(shown.otherCurrencyText).toBeNull();
  });

  it('uma fatura só, e nenhuma', () => {
    expect(upcomingBillsCard(upcoming({ totalBrl: 100.1, bills: [due({ totalAmount: 100.1 })] }))!.countText).toBe('1 fatura · vence em 27/10/2026');
    const none = upcomingBillsCard(upcoming({ totalBrl: 0, bills: [] }))!;
    expect(plain(none.totalText)).toBe('R$ 0,00');
    expect(none.countText).toBe('Nenhuma fatura a vencer');
  });

  it('fatura em outra moeda fica à parte, na moeda dela, e não conta como reais', () => {
    const shown = upcomingBillsCard(
      upcoming({
        totalBrl: 100.1,
        bills: [due({ id: 'b-1', totalAmount: 100.1 }), due({ id: 'b-usd', totalAmount: 50, currency: 'USD' }), due({ id: 'b-eur', totalAmount: 20, currency: 'EUR' })],
      }),
    )!;

    expect(plain(shown.totalText)).toBe('R$ 100,10');
    expect(shown.countText).toBe('1 fatura · vence em 27/10/2026');
    expect(plain(shown.otherCurrencyText)).toBe('Fora do total, em outra moeda: US$ 50,00 e € 20,00');
  });

  it('só fatura em outra moeda: total zero em reais e a outra à parte', () => {
    const shown = upcomingBillsCard(upcoming({ totalBrl: 0, bills: [due({ totalAmount: 50, currency: 'USD' })] }))!;
    expect(plain(shown.totalText)).toBe('R$ 0,00');
    expect(shown.countText).toBe('Nenhuma fatura em reais a vencer');
    expect(plain(shown.otherCurrencyText)).toBe('Fora do total, em outra moeda: US$ 50,00');
  });

  it('total que não é número (resposta estragada) vira zero, nunca "NaN"', () => {
    expect(plain(upcomingBillsCard(upcoming({ totalBrl: undefined as unknown as number, bills: [] }))!.totalText)).toBe('R$ 0,00');
  });
});
