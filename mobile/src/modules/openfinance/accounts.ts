// Open Finance (Meu Pluggy): regras da tela "Contas" e do cartão "Faturas a vencer" do Painel. Lógica pura (sem
// React Native), coberta por __tests__/accounts.test.ts.
//
// O que uma pessoa conecta é do grupo: todos veem as contas, os saldos e as faturas de todos. Nada aqui converte
// moeda: só o que é BRL entra em soma e sai como reais; o resto aparece na própria moeda, à parte.
import type {
  BankAccountsOverviewResponse,
  BankBalanceAccountResponse,
  BankCardResponse,
  CardBillResponse,
  UpcomingBillsResponse,
} from '@/types/api';
import { dayLabel, lineAmountText } from './review';

export interface PersonSection {
  readonly userId: string;
  /** "Você" para quem está logado; o nome para os outros. */
  readonly title: string;
  readonly isMe: boolean;
  readonly accounts: readonly BankBalanceAccountResponse[];
  readonly cards: readonly BankCardResponse[];
  /** Soma dos saldos das contas em reais. Cartão não entra (o saldo do cartão não é dinheiro em conta). */
  readonly balanceBrl: number;
  /** Há conta em outra moeda: ela aparece na lista e fica fora de `balanceBrl`. */
  readonly hasOtherCurrency: boolean;
}

/**
 * As contas agrupadas por pessoa: quem está logado primeiro, depois os outros por nome. Quem não tem conta nem
 * cartão não vira seção. Resposta ausente (servidor sem a rota) é lista vazia.
 */
export function peopleSections(response: BankAccountsOverviewResponse | null | undefined): PersonSection[] {
  const people = response?.people ?? [];
  return people
    .filter((person) => (person.accounts?.length ?? 0) + (person.cards?.length ?? 0) > 0)
    .map((person) => {
      const accounts = person.accounts ?? [];
      return {
        userId: person.userId,
        title: person.isMe ? 'Você' : person.userName,
        isMe: person.isMe,
        accounts,
        cards: person.cards ?? [],
        balanceBrl: accounts.filter((account) => account.currency === 'BRL').reduce((sum, account) => sum + account.balance, 0),
        hasOtherCurrency: accounts.some((account) => account.currency !== 'BRL'),
      };
    })
    .sort((a, b) => {
      if (a.isMe !== b.isMe) return a.isMe ? -1 : 1;
      return a.title.localeCompare(b.title, 'pt-BR', { sensitivity: 'base' }) || (a.userId < b.userId ? -1 : a.userId > b.userId ? 1 : 0);
    });
}

const MINUTE_MS = 60 * 1000;
const HOUR_MS = 60 * MINUTE_MS;
const DAY_MS = 24 * HOUR_MS;

/**
 * "atualizado há 5 min" a partir do instante em que o banco deu o saldo. Sempre para baixo (59 min 59 s é "59 min").
 * Menos de um minuto, ou um instante no futuro (relógio do aparelho atrasado), é "agora". null: sem instante.
 */
export function updatedAgoText(balanceAtUtc: string | null | undefined, nowMs: number): string | null {
  if (!balanceAtUtc) return null;
  const at = Date.parse(balanceAtUtc);
  if (!Number.isFinite(at)) return null;
  const elapsed = nowMs - at;
  if (elapsed < MINUTE_MS) return 'atualizado agora';
  if (elapsed < HOUR_MS) return `atualizado há ${Math.floor(elapsed / MINUTE_MS)} min`;
  if (elapsed < DAY_MS) return `atualizado há ${Math.floor(elapsed / HOUR_MS)} h`;
  const days = Math.floor(elapsed / DAY_MS);
  return `atualizado há ${days} ${days === 1 ? 'dia' : 'dias'}`;
}

export function accountKindLabel(account: Pick<BankBalanceAccountResponse, 'subtype'>): string {
  if (account.subtype === 'CHECKING_ACCOUNT') return 'Conta corrente';
  if (account.subtype === 'SAVINGS_ACCOUNT') return 'Poupança';
  return 'Conta';
}

/** "Vence em 27/10/2026 · fechou em 20/10/2026". As datas são dias de calendário, como o banco escreveu. */
export function billDatesText(bill: Pick<CardBillResponse, 'dueDate' | 'closingDate' | 'isClosed'>): string {
  const due = `Vence em ${dayLabel(bill.dueDate)}`;
  if (bill.closingDate) return `${due} · ${bill.isClosed ? 'fechou' : 'fecha'} em ${dayLabel(bill.closingDate)}`;
  return bill.isClosed ? `${due} · fechada` : due;
}

/** O limite do cartão, na moeda do cartão. null: o banco não informou limite nenhum. */
export function cardLimitText(card: Pick<BankCardResponse, 'creditLimit' | 'availableCreditLimit' | 'currency'>): string | null {
  const total = typeof card.creditLimit === 'number' ? lineAmountText(card.creditLimit, card.currency) : null;
  const available = typeof card.availableCreditLimit === 'number' ? lineAmountText(card.availableCreditLimit, card.currency) : null;
  if (available && total) return `Limite disponível ${available} de ${total}`;
  if (available) return `Limite disponível ${available}`;
  if (total) return `Limite ${total}`;
  return null;
}

export interface UpcomingBillsCard {
  /** O total em reais, como o servidor somou (só faturas em BRL). */
  readonly totalBrl: number;
  readonly totalText: string;
  /** "2 faturas · a primeira vence em 27/10/2026". Conta só as que entram no total: em reais e de valor maior que zero. */
  readonly countText: string;
  /** As faturas em outra moeda, cada uma na sua, fora do total. null: nenhuma. */
  readonly otherCurrencyText: string | null;
}

function joinWithE(parts: readonly string[]): string {
  if (parts.length <= 1) return parts.join('');
  return `${parts.slice(0, -1).join(', ')} e ${parts[parts.length - 1]}`;
}

/**
 * O cartão "Faturas a vencer nos próximos 30 dias" do Painel. null: o servidor não mandou o campo (ninguém do
 * grupo está conectado, ou é um servidor anterior a ele) e o cartão não aparece.
 */
export function upcomingBillsCard(upcoming: UpcomingBillsResponse | null | undefined): UpcomingBillsCard | null {
  if (!upcoming) return null;
  const bills = upcoming.bills ?? [];
  const inReais = bills.filter((bill) => bill.currency === 'BRL');
  // As que o servidor soma: fatura de zero ou de crédito (valor negativo) vem na lista e fica fora do total.
  const counted = inReais.filter((bill) => typeof bill.totalAmount === 'number' && bill.totalAmount > 0);
  const others = bills.filter((bill) => bill.currency !== 'BRL');
  const totalBrl = typeof upcoming.totalBrl === 'number' && Number.isFinite(upcoming.totalBrl) ? upcoming.totalBrl : 0;

  let countText: string;
  if (counted.length === 0) {
    countText = others.length > 0 ? 'Nenhuma fatura em reais a vencer' : 'Nenhuma fatura a vencer';
  } else {
    const first = counted.reduce((earliest, bill) => (bill.dueDate < earliest.dueDate ? bill : earliest));
    countText =
      counted.length === 1
        ? `1 fatura · vence em ${dayLabel(first.dueDate)}`
        : `${counted.length} faturas · a primeira vence em ${dayLabel(first.dueDate)}`;
  }

  return {
    totalBrl,
    totalText: lineAmountText(totalBrl, 'BRL'),
    countText,
    otherCurrencyText:
      others.length > 0
        ? `Fora do total, em outra moeda: ${joinWithE(others.map((bill) => lineAmountText(bill.totalAmount, bill.currency)))}`
        : null,
  };
}
