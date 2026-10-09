// Open Finance (issue #25): regras puras da tela "Revisão do banco". Tudo aqui é inventado ("Cantina Exemplo",
// ids falsos); nenhum dado bancário real.
import {
  CONFIRM_BATCH_SIZE,
  allSelected,
  buildConfirmBatches,
  confirmInBatches,
  confirmResultMessage,
  dayLabel,
  groupByDay,
  initialReviewMonth,
  installmentText,
  isSelectable,
  lineAmountText,
  lineTitle,
  monthTitle,
  pruneSelection,
  reviewDoneLabel,
  reviewShortcutLabel,
  selectAllIds,
  selectedTotalBrl,
  shiftMonth,
  toggleSelected,
  unselectableReason,
} from '../review';
import type { BankReviewLineResponse, ConfirmBankReviewRequest, ConfirmBankReviewResponse } from '@/types/api';

const line = (over: Partial<BankReviewLineResponse>): BankReviewLineResponse => ({
  id: 'l-1',
  day: '2026-10-05',
  merchant: 'Cantina Exemplo Ltda',
  description: 'Cantina Exemplo',
  amount: 58.9,
  currency: 'BRL',
  suggestedCategory: 'ALIMENTACAO',
  bankStatus: 'Posted',
  bankName: 'Banco Exemplo',
  accountName: 'Conta Corrente',
  installmentNumber: null,
  installmentTotal: null,
  ...over,
});

const posted = line({ id: 'a' });
const other = line({ id: 'b', day: '2026-10-03', amount: 23.4, suggestedCategory: 'TRANSPORTE', merchant: null, description: 'Corrida Exemplo' });
const pendingAtBank = line({ id: 'p', day: '2026-10-06', amount: 45, bankStatus: 'Pending', suggestedCategory: 'SAUDE' });
const dollars = line({ id: 'u', day: '2026-10-05', amount: 25, currency: 'USD', suggestedCategory: 'OUTROS' });
const lines = [pendingAtBank, posted, dollars, other];

describe('Selecionar tudo', () => {
  it('marca todas as despesas, menos as que ainda estão pendentes no banco e as em outra moeda', () => {
    expect(selectAllIds(lines)).toEqual(['a', 'b']);
    expect(selectAllIds(lines)).not.toContain('p');
    expect(selectAllIds(lines)).not.toContain('u');
  });

  it('lançamento pendente no banco não é selecionável, nem por toque', () => {
    expect(isSelectable(pendingAtBank)).toBe(false);
    expect(isSelectable(posted)).toBe(true);
    expect([...toggleSelected(new Set<string>(), pendingAtBank)]).toEqual([]);
    expect([...toggleSelected(new Set(['a']), pendingAtBank)]).toEqual(['a']);
  });

  it('o toque marca e desmarca uma linha sem mexer nas outras, e não muda o conjunto recebido', () => {
    const before = new Set(['a']);
    const added = toggleSelected(before, other);
    expect([...added].sort()).toEqual(['a', 'b']);
    expect([...before]).toEqual(['a']);
    expect([...toggleSelected(added, posted)]).toEqual(['b']);
  });

  it('"tudo selecionado" olha só o que pode ser selecionado; sem nada selecionável é falso', () => {
    expect(allSelected(lines, new Set(['a', 'b']))).toBe(true);
    expect(allSelected(lines, new Set(['a', 'u']))).toBe(false);
    expect(allSelected([pendingAtBank], new Set())).toBe(false);
    expect(allSelected([], new Set())).toBe(false);
  });

  it('a seleção perde o que saiu da lista, passou a estar pendente no banco ou está em outra moeda', () => {
    const selection = new Set(['a', 'b', 'gone', 'p', 'u']);
    expect([...pruneSelection(selection, lines)].sort()).toEqual(['a', 'b']);
  });

  it('o total selecionado soma só reais', () => {
    expect(selectedTotalBrl(lines, new Set(['a', 'u', 'b']))).toBeCloseTo(82.3, 2);
    expect(selectedTotalBrl(lines, new Set(['u']))).toBe(0);
    expect(selectedTotalBrl(lines, new Set())).toBe(0);
  });
});

describe('compra em outra moeda (revisão 5, I1)', () => {
  it('não é selecionável, nem por toque: nesta fase só despesa em reais é confirmada', () => {
    expect(isSelectable(dollars)).toBe(false);
    expect(isSelectable(line({ currency: 'EUR' }))).toBe(false);
    expect(isSelectable(line({ currency: '' }))).toBe(false);
    expect([...toggleSelected(new Set<string>(), dollars)]).toEqual([]);
    expect([...toggleSelected(new Set(['a']), dollars)]).toEqual(['a']);
  });

  it('a linha diz o motivo, em português, e que pode ser descartada', () => {
    expect(unselectableReason(dollars)).toBe('Em outra moeda: ainda não pode virar despesa. Você pode descartar.');
    // Pendente no banco vem primeiro: é o que muda sozinho.
    expect(unselectableReason(line({ currency: 'USD', bankStatus: 'Pending' }))).toBe(
      'Pendente no banco: poderá ser confirmado quando o banco efetivar.',
    );
    expect(unselectableReason(line({ currency: 'USD', amount: 0 }))).toBe('Sem valor: não vira despesa. Você pode descartar.');
  });

  it('nunca entra num lote de confirmação nem no total em reais, mesmo que estivesse marcada', () => {
    const forced = new Set(['a', 'u', 'b']);
    expect(buildConfirmBatches(lines, forced, { u: 'LAZER' })).toEqual([{ expenses: [{ id: 'a' }, { id: 'b' }] }]);
    expect(selectedTotalBrl(lines, forced)).toBeCloseTo(82.3, 2);
    expect(buildConfirmBatches([dollars], new Set(['u']), {})).toEqual([]);
  });
});

describe('o valor da linha nunca aparece como reais sem ser reais (revisão 5, releitura)', () => {
  const plain = (text: string) => text.replace(/\u00a0/g, ' ');

  it('reais saem como reais; outra moeda, com o símbolo dela', () => {
    expect(plain(lineAmountText(58.9, 'BRL'))).toBe('R$ 58,90');
    expect(plain(lineAmountText(25, 'USD'))).toBe('US$ 25,00');
    expect(plain(lineAmountText(25, 'USD'))).not.toContain('R$');
  });

  it('sem moeda: só o número, sem "R$"; moeda que o aparelho não conhece: o código e o número', () => {
    expect(lineAmountText(25, '')).toBe('25,00');
    expect(lineAmountText(25, null)).toBe('25,00');
    expect(lineAmountText(25, undefined)).toBe('25,00');
    expect(lineAmountText(25, 'X1')).toBe('X1 25,00');
  });
});

describe('lançamento sem valor (revisão 1, I5)', () => {
  const zero = line({ id: 'z', amount: 0, description: 'Tarifa Exemplo Zerada', merchant: null });
  const withZero = [posted, zero, other];

  it('não é selecionável, nem por toque, e "Selecionar tudo" não o marca', () => {
    expect(isSelectable(zero)).toBe(false);
    expect(selectAllIds(withZero)).toEqual(['a', 'b']);
    expect([...toggleSelected(new Set<string>(), zero)]).toEqual([]);
    expect(allSelected(withZero, new Set(['a', 'b']))).toBe(true);
  });

  it('nunca entra num lote de confirmação nem no total, mesmo que estivesse marcado', () => {
    const forced = new Set(['a', 'z', 'b']);
    expect(buildConfirmBatches(withZero, forced, {})).toEqual([{ expenses: [{ id: 'a' }, { id: 'b' }] }]);
    expect([...pruneSelection(forced, withZero)]).toEqual(['a', 'b']);
    expect(selectedTotalBrl(withZero, forced)).toBeCloseTo(82.3);
  });

  it('a linha diz por que não pode ser confirmada: sem valor, ou pendente no banco', () => {
    expect(unselectableReason(zero)).toBe('Sem valor: não vira despesa. Você pode descartar.');
    expect(unselectableReason(pendingAtBank)).toBe('Pendente no banco: poderá ser confirmado quando o banco efetivar.');
    expect(unselectableReason(posted)).toBeNull();
  });
});

describe('lotes de confirmação', () => {
  it('monta um lote com as selecionadas, na ordem da tela, sem as pendentes no banco', () => {
    const batches = buildConfirmBatches(lines, new Set(['b', 'a', 'p']), {});
    expect(batches).toEqual([{ expenses: [{ id: 'a' }, { id: 'b' }] }]);
  });

  it('manda a categoria só quando a pessoa escolheu outra que não a sugerida', () => {
    const reais = line({ id: 'r', day: '2026-10-05', amount: 12, suggestedCategory: 'OUTROS' });
    const batches = buildConfirmBatches([posted, reais, other], new Set(['a', 'b', 'r']), { a: 'LAZER', b: 'TRANSPORTE', gone: 'SAUDE' });
    expect(batches).toEqual([{ expenses: [{ id: 'a', category: 'LAZER' }, { id: 'r' }, { id: 'b' }] }]);
  });

  it('nunca manda valor nem descrição: o valor não é editável', () => {
    const [batch] = buildConfirmBatches(lines, new Set(['a']), { a: 'LAZER' });
    expect(Object.keys(batch.expenses![0]).sort()).toEqual(['category', 'id']);
    expect(batch).not.toHaveProperty('discard');
  });

  it('divide em lotes de 200, sem perder nem repetir linha', () => {
    expect(CONFIRM_BATCH_SIZE).toBe(200);
    const many = Array.from({ length: 450 }, (_, i) => line({ id: `l-${i}`, amount: i + 1 }));
    const batches = buildConfirmBatches(many, new Set(many.map((l) => l.id)), {});
    expect(batches.map((b) => b.expenses!.length)).toEqual([200, 200, 50]);
    const sent = batches.flatMap((b) => b.expenses!.map((e) => e.id));
    expect(sent).toEqual(many.map((l) => l.id));
    expect(new Set(sent).size).toBe(450);
  });

  it('exatamente 200 é um lote só; nada selecionado é nenhum lote', () => {
    const many = Array.from({ length: 200 }, (_, i) => line({ id: `l-${i}` }));
    expect(buildConfirmBatches(many, new Set(many.map((l) => l.id)), {})).toHaveLength(1);
    expect(buildConfirmBatches(lines, new Set(), {})).toEqual([]);
    expect(buildConfirmBatches(lines, new Set(['p']), {})).toEqual([]);
  });

  it('aceita outro tamanho de lote', () => {
    const third = line({ id: 'c', day: '2026-10-01' });
    const batches = buildConfirmBatches([posted, other, third], new Set(['a', 'b', 'c']), {}, 2);
    expect(batches.map((b) => b.expenses!.map((e) => e.id))).toEqual([['a', 'b'], ['c']]);
  });
});

describe('"Confirmar selecionadas": um lote por vez (revisão 5, I3)', () => {
  const batchOf = (...ids: string[]): ConfirmBankReviewRequest => ({ expenses: ids.map((id) => ({ id })) });
  const three = [batchOf('a1', 'a2'), batchOf('b1', 'b2'), batchOf('c1')];
  const answer = (over: Partial<ConfirmBankReviewResponse>): ConfirmBankReviewResponse => ({
    created: [],
    discarded: [],
    alreadyConfirmed: 0,
    skipped: [],
    ...over,
  });
  const createdFor = (batch: ConfirmBankReviewRequest) => (batch.expenses ?? []).map((e) => ({ id: e.id, transactionId: `t-${e.id}` }));

  it('manda os lotes em ordem, um depois do outro, e soma o que cada um respondeu', async () => {
    const sent: ConfirmBankReviewRequest[] = [];
    const answers = [
      answer({ created: createdFor(three[0]) }),
      answer({ created: [{ id: 'b1', transactionId: 't-b1' }], alreadyConfirmed: 1 }),
      answer({ skipped: ['c1'] }),
    ];

    const outcome = await confirmInBatches(three, {
      confirm: async (batch) => {
        sent.push(batch);
        return answers[sent.length - 1];
      },
      isCurrent: () => true,
    });

    expect(sent).toEqual(three);
    expect(outcome).toEqual({ kind: 'done', text: '3 despesas registradas. 1 já estava registrada. 1 sem valor continua na revisão.' });
  });

  it('erro no 2º de 3 lotes: para ali, diz quantas entraram e o motivo, e o 3º não é enviado', async () => {
    const sent: ConfirmBankReviewRequest[] = [];

    const outcome = await confirmInBatches(three, {
      confirm: async (batch) => {
        sent.push(batch);
        if (sent.length === 2) throw { response: { status: 500 } };
        return answer({ created: createdFor(batch) });
      },
      isCurrent: () => true,
    });

    expect(sent).toEqual([three[0], three[1]]);
    expect(outcome).toEqual({
      kind: 'failed',
      text: '2 despesas registradas. O restante não entrou: Servidor com problemas. Tente novamente mais tarde.',
    });
  });

  it('409 de conflito no meio: para, e mostra a mensagem do servidor depois do que já entrou', async () => {
    const sent: ConfirmBankReviewRequest[] = [];
    const conflict = {
      response: {
        status: 409,
        data: { code: 'BANK_REVIEW_CONFLICT', message: 'A revisão mudou enquanto era confirmada. Atualize e tente de novo.' },
      },
    };

    const outcome = await confirmInBatches(three, {
      confirm: async (batch) => {
        sent.push(batch);
        if (sent.length === 2) throw conflict;
        return answer({ created: [{ id: 'a1', transactionId: 't-a1' }], alreadyConfirmed: 1 });
      },
      isCurrent: () => true,
    });

    expect(sent).toHaveLength(2);
    expect(outcome).toEqual({
      kind: 'failed',
      text: '1 despesa registrada. 1 já estava registrada. O restante não entrou: A revisão mudou enquanto era confirmada. Atualize e tente de novo.',
    });
  });

  it('erro já no 1º lote: só o motivo (nada entrou), e nenhum outro lote é enviado', async () => {
    const confirm = jest.fn(async () => {
      throw { response: { status: 422, data: { code: 'TRANSACTION_NOT_POSTED', message: 'Este lançamento ainda está pendente no banco.' } } };
    });

    const outcome = await confirmInBatches(three, { confirm, isCurrent: () => true });

    expect(confirm).toHaveBeenCalledTimes(1);
    expect(outcome).toEqual({ kind: 'failed', text: 'Este lançamento ainda está pendente no banco.' });
  });

  it('erro que não diz nada (nem resposta, nem rede): o texto fixo', async () => {
    const outcome = await confirmInBatches([three[0]], {
      confirm: async () => {
        throw new Error('qualquer coisa');
      },
      isCurrent: () => true,
    });

    expect(outcome).toEqual({ kind: 'failed', text: 'Não foi possível confirmar. Tente novamente.' });
  });

  it('linhas puladas antes do erro, sem nenhuma registrada: só o motivo', async () => {
    let calls = 0;
    const outcome = await confirmInBatches(three, {
      confirm: async () => {
        calls += 1;
        if (calls === 2) throw { response: { status: 500 } };
        return answer({ skipped: ['a1', 'a2'] });
      },
      isCurrent: () => true,
    });

    expect(outcome).toEqual({ kind: 'failed', text: 'Servidor com problemas. Tente novamente mais tarde.' });
  });

  it('sessão trocada no meio (saiu da conta, trocou de grupo): para sem mandar o lote seguinte e sem aviso', async () => {
    let current = true;
    const sent: ConfirmBankReviewRequest[] = [];

    const outcome = await confirmInBatches(three, {
      confirm: async (batch) => {
        sent.push(batch);
        current = false; // a sessão muda enquanto o 1º lote está no servidor
        return answer({ created: createdFor(batch) });
      },
      isCurrent: () => current,
    });

    expect(sent).toEqual([three[0]]);
    expect(outcome).toEqual({ kind: 'stale' });
  });

  it('sessão trocada enquanto um lote falhava: o erro da sessão antiga não vira aviso', async () => {
    let current = true;

    const outcome = await confirmInBatches(three, {
      confirm: async () => {
        current = false;
        throw { response: { status: 401 } };
      },
      isCurrent: () => current,
    });

    expect(outcome).toEqual({ kind: 'stale' });
  });

  it('servidor que ainda não manda `skipped`: conta como nenhuma pulada', async () => {
    const outcome = await confirmInBatches([three[0]], {
      confirm: async (batch) => ({ created: createdFor(batch), discarded: [], alreadyConfirmed: 0 }),
      isCurrent: () => true,
    });

    expect(outcome).toEqual({ kind: 'done', text: '2 despesas registradas.' });
  });

  it('linhas em outra moeda puladas pelo servidor aparecem com o motivo delas', async () => {
    const outcome = await confirmInBatches([three[0], three[1]], {
      confirm: async (batch) =>
        batch === three[0]
          ? answer({ created: [{ id: 'a1', transactionId: 't-a1' }], skipped: ['a2'], skippedOtherCurrency: ['a2'] })
          : answer({ skipped: ['b1', 'b2'], skippedOtherCurrency: ['b2'] }),
      isCurrent: () => true,
    });

    expect(outcome).toEqual({
      kind: 'done',
      text: '1 despesa registrada. 1 sem valor continua na revisão. 2 em outra moeda continuam na revisão.',
    });
  });
});

describe('linhas por dia', () => {
  it('agrupa por dia, do mais recente para o mais antigo, mantendo a ordem dentro do dia', () => {
    const groups = groupByDay(lines);
    expect(groups.map((g) => g.day)).toEqual(['2026-10-06', '2026-10-05', '2026-10-03']);
    expect(groups[1].lines.map((l) => l.id)).toEqual(['a', 'u']);
    expect(groups[0].label).toBe('06/10/2026');
  });

  it('o dia é mostrado como está escrito, sem passar pelo fuso do aparelho', () => {
    expect(dayLabel('2026-10-01')).toBe('01/10/2026');
    expect(dayLabel('2026-01-31')).toBe('31/01/2026');
    expect(dayLabel('qualquer coisa')).toBe('qualquer coisa');
  });

  it('o título da linha é o estabelecimento; sem ele, a descrição; sem nenhum, um texto fixo', () => {
    expect(lineTitle(posted)).toBe('Cantina Exemplo Ltda');
    expect(lineTitle(other)).toBe('Corrida Exemplo');
    expect(lineTitle(line({ merchant: '  ', description: null }))).toBe('Lançamento do banco');
  });

  it('parcela aparece só quando o banco informa as duas partes', () => {
    expect(installmentText(line({ installmentNumber: 2, installmentTotal: 6 }))).toBe('parcela 2 de 6');
    expect(installmentText(line({ installmentNumber: 2, installmentTotal: null }))).toBeNull();
    expect(installmentText(posted)).toBeNull();
  });
});

describe('mês da revisão', () => {
  it('abre no mês mais recente que tem algo esperando; sem nada, no mês corrente', () => {
    const pending = [
      { month: '2026-10', pending: 3 },
      { month: '2026-08', pending: 12 },
    ];
    expect(initialReviewMonth(pending, '2026-11')).toBe('2026-10');
    expect(initialReviewMonth([{ month: '2026-08', pending: 12 }], '2026-10')).toBe('2026-08');
    expect(initialReviewMonth([], '2026-10')).toBe('2026-10');
    expect(initialReviewMonth(undefined, '2026-10')).toBe('2026-10');
    expect(initialReviewMonth([{ month: '2026-09', pending: 0 }], '2026-10')).toBe('2026-10');
  });

  it('anda um mês para trás e para frente, virando o ano', () => {
    expect(shiftMonth('2026-10', -1)).toBe('2026-09');
    expect(shiftMonth('2026-01', -1)).toBe('2025-12');
    expect(shiftMonth('2026-12', 1)).toBe('2027-01');
    expect(shiftMonth('2026-10', 0)).toBe('2026-10');
  });

  it('o título do mês vem em português', () => {
    expect(monthTitle('2026-10')).toBe('outubro de 2026');
    expect(monthTitle('2026-03')).toBe('março de 2026');
  });
});

describe('textos', () => {
  it('o atalho da tela de Transações só existe quando há algo esperando', () => {
    expect(reviewShortcutLabel(0)).toBeNull();
    expect(reviewShortcutLabel(undefined)).toBeNull();
    expect(reviewShortcutLabel(-2)).toBeNull();
    expect(reviewShortcutLabel(1)).toBe('1 do banco para revisar');
    expect(reviewShortcutLabel(37)).toBe('37 do banco para revisar');
  });

  it('o fim do wizard diz quantas transações esperam a revisão', () => {
    expect(reviewDoneLabel(0)).toBe('Nenhuma transação para revisar agora');
    expect(reviewDoneLabel(1)).toBe('Ver 1 transação para revisar');
    expect(reviewDoneLabel(128)).toBe('Ver 128 transações para revisar');
  });

  it('o aviso depois de confirmar conta o que entrou e o que já estava lá', () => {
    expect(confirmResultMessage(1, 0)).toBe('1 despesa registrada.');
    expect(confirmResultMessage(12, 0)).toBe('12 despesas registradas.');
    expect(confirmResultMessage(3, 2)).toBe('3 despesas registradas. 2 já estavam registradas.');
    expect(confirmResultMessage(0, 1)).toBe('1 já estava registrada.');
    expect(confirmResultMessage(0, 0)).toBe('Nada a registrar.');
  });

  it('o aviso diz quando o servidor pulou linhas sem valor (elas continuam na revisão)', () => {
    expect(confirmResultMessage(2, 0, 1)).toBe('2 despesas registradas. 1 sem valor continua na revisão.');
    expect(confirmResultMessage(0, 0, 3)).toBe('3 sem valor continuam na revisão.');
    expect(confirmResultMessage(2, 1, 0)).toBe('2 despesas registradas. 1 já estava registrada.');
  });

  it('o aviso separa as puladas por estarem em outra moeda das sem valor', () => {
    expect(confirmResultMessage(2, 0, 1, 1)).toBe('2 despesas registradas. 1 em outra moeda continua na revisão.');
    expect(confirmResultMessage(0, 0, 5, 3)).toBe('2 sem valor continuam na revisão. 3 em outra moeda continuam na revisão.');
    // Nunca um número negativo de "sem valor", venha o que vier.
    expect(confirmResultMessage(1, 0, 1, 4)).toBe('1 despesa registrada. 4 em outra moeda continuam na revisão.');
  });
});
