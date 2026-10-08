// Open Finance (issue #25): regras puras da tela "Revisão do banco". Tudo aqui é inventado ("Cantina Exemplo",
// ids falsos); nenhum dado bancário real.
import {
  CONFIRM_BATCH_SIZE,
  allSelected,
  buildConfirmBatches,
  confirmResultMessage,
  dayLabel,
  groupByDay,
  initialReviewMonth,
  installmentText,
  isSelectable,
  lineTitle,
  monthTitle,
  pruneSelection,
  reviewDoneLabel,
  reviewShortcutLabel,
  selectAllIds,
  selectedTotalBrl,
  shiftMonth,
  toggleSelected,
} from '../review';
import type { BankReviewLineResponse } from '@/types/api';

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
  it('marca todas as despesas, menos as que ainda estão pendentes no banco', () => {
    expect(selectAllIds(lines)).toEqual(['a', 'u', 'b']);
    expect(selectAllIds(lines)).not.toContain('p');
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
    expect(allSelected(lines, new Set(['a', 'u', 'b']))).toBe(true);
    expect(allSelected(lines, new Set(['a', 'u']))).toBe(false);
    expect(allSelected([pendingAtBank], new Set())).toBe(false);
    expect(allSelected([], new Set())).toBe(false);
  });

  it('a seleção perde o que saiu da lista ou passou a estar pendente no banco', () => {
    const selection = new Set(['a', 'b', 'gone', 'p']);
    expect([...pruneSelection(selection, lines)].sort()).toEqual(['a', 'b']);
  });

  it('o total selecionado soma só reais', () => {
    expect(selectedTotalBrl(lines, new Set(['a', 'u', 'b']))).toBeCloseTo(82.3, 2);
    expect(selectedTotalBrl(lines, new Set(['u']))).toBe(0);
    expect(selectedTotalBrl(lines, new Set())).toBe(0);
  });
});

describe('lotes de confirmação', () => {
  it('monta um lote com as selecionadas, na ordem da tela, sem as pendentes no banco', () => {
    const batches = buildConfirmBatches(lines, new Set(['b', 'a', 'p']), {});
    expect(batches).toEqual([{ expenses: [{ id: 'a' }, { id: 'b' }] }]);
  });

  it('manda a categoria só quando a pessoa escolheu outra que não a sugerida', () => {
    const batches = buildConfirmBatches(lines, new Set(['a', 'b', 'u']), { a: 'LAZER', b: 'TRANSPORTE', gone: 'SAUDE' });
    expect(batches).toEqual([{ expenses: [{ id: 'a', category: 'LAZER' }, { id: 'u' }, { id: 'b' }] }]);
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
    const batches = buildConfirmBatches(lines, new Set(['a', 'u', 'b']), {}, 2);
    expect(batches.map((b) => b.expenses!.map((e) => e.id))).toEqual([['a', 'u'], ['b']]);
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
});
