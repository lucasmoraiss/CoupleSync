// Open Finance (issue #26): regras puras da conciliação na tela "Revisão do banco": possíveis duplicadas
// ("É a mesma" / "É outra") e a seção Ignoradas. Tudo aqui é inventado; nenhum dado bancário real.
// O Jest daqui não renderiza React Native: o que é da tela (quem tem o botão "restaurar") é conferido lendo o
// código dela, como nos testes de navegação.
import * as fs from 'fs';
import * as path from 'path';
import * as review from '../review';
import {
  allSelected,
  answerableDuplicateCount,
  answerDuplicateNotice,
  answerDuplicateRequest,
  answerDuplicateResultMessage,
  autoReasonText,
  buildConfirmBatches,
  canAnswerDuplicate,
  chosenOfHeld,
  confirmInBatches,
  duplicateNoticeText,
  hasSuspectedDuplicate,
  heldDuplicatesText,
  ignoredLineText,
  isSelectable,
  matchText,
  pruneSelection,
  restoreLabel,
  restoredNoticeText,
  selectAllIds,
  selectedTotalBrl,
  suspectedDuplicateCount,
  toggleSelected,
} from '../review';
import type {
  BankReviewLineResponse,
  BankReviewMatchResponse,
  ConfirmBankReviewRequest,
  ConfirmBankReviewResponse,
} from '@/types/api';

const line = (over: Partial<BankReviewLineResponse>): BankReviewLineResponse => ({
  id: 'l-1',
  day: '2026-10-04',
  merchant: 'Padaria Exemplo Ltda',
  description: 'Padaria Exemplo',
  amount: 25,
  currency: 'BRL',
  suggestedCategory: 'ALIMENTACAO',
  bankStatus: 'Posted',
  bankName: 'Banco Exemplo',
  accountName: 'Conta Corrente',
  installmentNumber: null,
  installmentTotal: null,
  ...over,
});

const match = (over: Partial<BankReviewMatchResponse> = {}): BankReviewMatchResponse => ({
  transactionId: 't-1',
  source: 'Notification',
  description: 'Compra no débito',
  merchant: 'Padaria',
  amount: 25,
  currency: 'BRL',
  day: '2026-10-03',
  ...over,
});

const plain = line({ id: 'a', amount: 80 });
const duplicate = line({ id: 'd', match: match() });
const otherPlain = line({ id: 'b', amount: 12.5 });
const duplicatePendingAtBank = line({ id: 'dp', bankStatus: 'Pending', match: match() });
const lines = [plain, duplicate, otherPlain, duplicatePendingAtBank];

const response = (over: Partial<ConfirmBankReviewResponse> = {}): ConfirmBankReviewResponse => ({
  created: [],
  discarded: [],
  alreadyConfirmed: 0,
  skipped: [],
  skippedOtherCurrency: [],
  reconciled: [],
  ...over,
});

describe('Selecionar tudo e possíveis duplicadas', () => {
  it('"Selecionar tudo" não marca as linhas com possível duplicada', () => {
    expect(selectAllIds(lines)).toEqual(['a', 'b']);
    expect(selectAllIds(lines)).not.toContain('d');
  });

  it('com tudo o que pode ser selecionado marcado, "Selecionar tudo" está completo mesmo sobrando a duplicada', () => {
    expect(allSelected(lines, new Set(['a', 'b']))).toBe(true);
    expect(allSelected(lines, new Set(['a']))).toBe(false);
    // Só duplicadas na lista: não há o que selecionar.
    expect(allSelected([duplicate], new Set<string>())).toBe(false);
    expect(selectAllIds([duplicate])).toEqual([]);
  });

  it('a linha com possível duplicada não é selecionável nem por toque: a resposta é "É a mesma" ou "É outra"', () => {
    expect(isSelectable(duplicate)).toBe(false);
    expect(isSelectable(plain)).toBe(true);
    expect([...toggleSelected(new Set<string>(), duplicate)]).toEqual([]);
    expect([...toggleSelected(new Set(['a']), duplicate)]).toEqual(['a']);
  });

  it('uma linha que estava selecionada e passou a ter possível duplicada sai da seleção ao recarregar', () => {
    const becameDuplicate = line({ id: 'a', amount: 80, match: match() });
    expect([...pruneSelection(new Set(['a', 'b']), [becameDuplicate, otherPlain])]).toEqual(['b']);
  });

  it('a duplicada nunca vai num lote de "Confirmar selecionadas" nem entra no total, mesmo com o id na seleção', () => {
    const selected = new Set(['a', 'd', 'b']);
    expect(buildConfirmBatches(lines, selected, {})).toEqual([{ expenses: [{ id: 'a' }, { id: 'b' }], holdPossibleDuplicates: true }]);
    expect(selectedTotalBrl(lines, selected)).toBeCloseTo(92.5, 2);
  });

  it('sem o campo match (servidor antigo) ou com match nulo, a linha é uma despesa comum', () => {
    expect(hasSuspectedDuplicate(plain)).toBe(false);
    expect(hasSuspectedDuplicate(line({ match: null }))).toBe(false);
    expect(hasSuspectedDuplicate(duplicate)).toBe(true);
    expect(isSelectable(line({ match: null }))).toBe(true);
  });
});

describe('aviso no topo', () => {
  it('conta as linhas da lista com possível duplicada', () => {
    expect(suspectedDuplicateCount(lines)).toBe(2);
    expect(suspectedDuplicateCount([plain, otherPlain])).toBe(0);
    expect(suspectedDuplicateCount([])).toBe(0);
  });

  it('o aviso conta só as que já podem ser respondidas: a pendente no banco tem os botões desabilitados e não entra', () => {
    const pendingAtBank = line({ id: 'l-9', bankStatus: 'Pending', match: duplicate.match });
    expect(canAnswerDuplicate(pendingAtBank)).toBe(false);
    expect(answerableDuplicateCount([plain, duplicate, pendingAtBank])).toBe(1);
    expect(suspectedDuplicateCount([plain, duplicate, pendingAtBank])).toBe(2);
    // Só pendentes no banco: nenhuma precisa da pessoa agora, sem aviso.
    expect(duplicateNoticeText(answerableDuplicateCount([plain, pendingAtBank]))).toBeNull();
    expect(answerableDuplicateCount([])).toBe(0);
  });

  it('"N possíveis duplicadas precisam de você", no singular e no plural; sem nenhuma, sem aviso', () => {
    expect(duplicateNoticeText(0)).toBeNull();
    expect(duplicateNoticeText(1)).toBe('1 possível duplicada precisa de você');
    expect(duplicateNoticeText(2)).toBe('2 possíveis duplicadas precisam de você');
    expect(duplicateNoticeText(13)).toBe('13 possíveis duplicadas precisam de você');
  });
});

describe('"Parece a mesma de"', () => {
  it('mostra o estabelecimento, o valor em reais, a origem e o dia da transação que já existe', () => {
    // O espaço entre "R$" e o número é o que o Intl do aparelho usar (normal ou inseparável).
    expect(matchText(match()).replace(/\u00a0/g, ' ')).toBe('Parece a mesma de: Padaria · R$ 25,00 · notificação de 03/10');
  });

  it.each([
    ['Manual', 'lançamento manual de 03/10'],
    ['Notification', 'notificação de 03/10'],
    ['OcrImport', 'extrato importado de 03/10'],
    ['Outra', 'registro de 03/10'],
  ])('origem %s aparece como "%s"', (source, expected) => {
    expect(matchText(match({ source }))).toContain(expected);
  });

  it('sem estabelecimento usa a descrição; sem os dois, um texto fixo', () => {
    expect(matchText(match({ merchant: null }))).toContain('Parece a mesma de: Compra no débito · ');
    expect(matchText(match({ merchant: '  ', description: null }))).toContain('Parece a mesma de: transação já registrada · ');
  });

  it('valor que não é em reais nunca aparece como reais', () => {
    const text = matchText(match({ currency: 'USD', amount: 25 }));
    expect(text).not.toContain('R$');
    expect(text).toMatch(/US\$|USD/);
    // Moeda que o aparelho não conhece: o código e o número.
    expect(matchText(match({ currency: 'XX', amount: 25 }))).toContain('XX 25,00');
  });

  it('um dia que não veio como AAAA-MM-DD aparece como veio', () => {
    expect(matchText(match({ day: 'ontem' }))).toContain('notificação de ontem');
  });
});

describe('"É a mesma" e "É outra"', () => {
  it('só podem ser respondidas quando o banco já efetivou o lançamento', () => {
    expect(canAnswerDuplicate(duplicate)).toBe(true);
    expect(canAnswerDuplicate(duplicatePendingAtBank)).toBe(false);
    // Sem possível duplicada não há o que responder.
    expect(canAnswerDuplicate(plain)).toBe(false);
  });

  it('"É a mesma" manda só o id em reconcile', () => {
    expect(answerDuplicateRequest('same', duplicate, {})).toEqual({ reconcile: [{ id: 'd' }] });
    // A categoria escolhida na tela não vai: nada é criado.
    expect(answerDuplicateRequest('same', duplicate, { d: 'LAZER' })).toEqual({ reconcile: [{ id: 'd' }] });
  });

  it('"É outra" manda o id em asNew; com outra categoria escolhida, manda em expenses com a categoria', () => {
    expect(answerDuplicateRequest('other', duplicate, {})).toEqual({ asNew: ['d'] });
    expect(answerDuplicateRequest('other', duplicate, { d: 'ALIMENTACAO' })).toEqual({ asNew: ['d'] });
    expect(answerDuplicateRequest('other', duplicate, { d: 'LAZER' })).toEqual({ expenses: [{ id: 'd', category: 'LAZER' }] });
    // A categoria escolhida para outra linha não conta.
    expect(answerDuplicateRequest('other', duplicate, { a: 'LAZER' })).toEqual({ asNew: ['d'] });
  });

  it('o aviso de "É a mesma" diz que nada novo foi criado', () => {
    expect(answerDuplicateResultMessage('same', response({ reconciled: [{ id: 'd', transactionId: 't-1' }] }))).toBe(
      'Marcada como a mesma: nenhuma despesa nova foi criada.',
    );
    // Resposta sem o campo (servidor antigo): nada a dizer de conciliado.
    expect(answerDuplicateResultMessage('same', { created: [], discarded: [], alreadyConfirmed: 0 })).toBe('Nada foi alterado.');
    expect(answerDuplicateResultMessage('same', response())).toBe('Nada foi alterado.');
  });

  it('"É a mesma" com outra categoria escolhida no chip não descarta a escolha em silêncio: avisa que a categoria da transação já registrada foi mantida', () => {
    const done = response({ reconciled: [{ id: 'd', transactionId: 't-1' }] });
    expect(answerDuplicateNotice('same', duplicate, { d: 'LAZER' }, done)).toBe(
      'Marcada como a mesma: nenhuma despesa nova foi criada. A categoria escolhida não foi usada: a transação que já estava no app manteve a dela.',
    );
    // Sem escolha, ou com a escolha igual à sugerida, ou para outra linha: nada a avisar além do de sempre.
    const plainNotice = 'Marcada como a mesma: nenhuma despesa nova foi criada.';
    expect(answerDuplicateNotice('same', duplicate, {}, done)).toBe(plainNotice);
    expect(answerDuplicateNotice('same', duplicate, { d: 'ALIMENTACAO' }, done)).toBe(plainNotice);
    expect(answerDuplicateNotice('same', duplicate, { a: 'LAZER' }, done)).toBe(plainNotice);
    // Se nada foi conciliado, a categoria não foi "mantida" de nada.
    expect(answerDuplicateNotice('same', duplicate, { d: 'LAZER' }, response())).toBe('Nada foi alterado.');
    // "É outra" usa a categoria escolhida: não há o que avisar.
    expect(answerDuplicateNotice('other', duplicate, { d: 'LAZER' }, response({ created: [{ id: 'd', transactionId: 't-2' }] }))).toBe(
      '1 despesa registrada.',
    );
  });

  it('o aviso de "É outra" é o de uma despesa confirmada, também quando o servidor a pulou', () => {
    expect(answerDuplicateResultMessage('other', response({ created: [{ id: 'd', transactionId: 't-2' }] }))).toBe('1 despesa registrada.');
    expect(answerDuplicateResultMessage('other', response({ alreadyConfirmed: 1 }))).toBe('1 já estava registrada.');
    expect(answerDuplicateResultMessage('other', response({ skipped: ['d'], skippedOtherCurrency: ['d'] }))).toBe(
      '1 em outra moeda continua na revisão.',
    );
  });
});

describe('Ignoradas', () => {
  it('cada motivo tem o seu texto', () => {
    expect(autoReasonText('Transfer')).toBe('Transferência entre contas do grupo');
    expect(autoReasonText('BillPayment')).toBe('Pagamento de fatura do cartão');
    expect(autoReasonText('Outro')).toBe('Ignorada automaticamente');
    expect(autoReasonText(null)).toBe('Ignorada automaticamente');
    expect(autoReasonText(undefined)).toBe('Ignorada automaticamente');
  });

  it('a linha mostra o dia, o valor na moeda dela, se é entrada, e o motivo', () => {
    const sent = line({ id: 'i1', day: '2026-10-05', amount: 500, type: 'Debit', autoReason: 'Transfer' });
    const received = line({ id: 'i2', day: '2026-10-05', amount: 500, type: 'Credit', autoReason: 'Transfer' });
    const bill = line({ id: 'i3', day: '2026-10-06', amount: 900, type: 'Debit', autoReason: 'BillPayment' });
    expect(ignoredLineText(sent).replace(/\u00a0/g, ' ')).toBe('05/10/2026 · R$ 500,00 · Transferência entre contas do grupo');
    expect(ignoredLineText(received).replace(/\u00a0/g, ' ')).toBe('05/10/2026 · entrada de R$ 500,00 · Transferência entre contas do grupo');
    expect(ignoredLineText(bill).replace(/\u00a0/g, ' ')).toBe('06/10/2026 · R$ 900,00 · Pagamento de fatura do cartão');
    // Outra moeda nunca aparece como reais.
    expect(ignoredLineText(line({ amount: 10, currency: 'USD', type: 'Debit', autoReason: 'Transfer' }))).not.toContain('R$');
  });

  it('a entrada restaurada some desta tela (que só lista despesas): o aviso diz isso; a saída volta para a lista, sem aviso', () => {
    expect(restoredNoticeText(line({ type: 'Credit', autoReason: 'Transfer' }))).toBe(
      'Entrada restaurada: ela deixou de ser ignorada. Esta lista é só de despesas, por isso ela não aparece aqui.',
    );
    expect(restoredNoticeText(line({ type: 'Debit', autoReason: 'Transfer' }))).toBeNull();
    expect(restoredNoticeText(line({ type: 'Debit', autoReason: 'BillPayment' }))).toBeNull();
    // Sem o campo (servidor antigo só lista despesas; as descartadas são sempre despesas): é despesa.
    expect(restoredNoticeText(line({}))).toBeNull();
  });

  it('o botão diz em voz alta o que restaura: "entrada" quando é dinheiro que entrou', () => {
    expect(restoreLabel(line({ type: 'Credit', merchant: null, description: 'PIX RECEBIDO' }))).toBe('Restaurar entrada PIX RECEBIDO');
    expect(restoreLabel(line({ type: 'Debit', merchant: null, description: 'PIX ENVIADO' }))).toBe('Restaurar PIX ENVIADO');
    expect(restoreLabel(line({}))).toBe('Restaurar Padaria Exemplo Ltda');
  });
});

describe('a tela: toda linha ignorada tem "restaurar", a entrada também', () => {
  const screen = fs.readFileSync(path.resolve(__dirname, '../../../../app/(main)/openfinance/review.tsx'), 'utf8');
  const start = screen.indexOf('ignored.map((line) => (');
  const end = screen.indexOf('{discarded.length > 0 ?');
  const ignoredRows = screen.slice(start, end);

  it('o trecho das ignoradas foi encontrado no código da tela', () => {
    expect(start).toBeGreaterThan(0);
    expect(end).toBeGreaterThan(start);
  });

  it('o botão "restaurar" está em toda linha ignorada, sem condição pelo tipo da linha', () => {
    expect(ignoredRows).toContain('onPress={() => void handleRestore(line)}');
    expect(ignoredRows).toContain('<Text style={styles.smallBtnText}>restaurar</Text>');
    expect(ignoredRows).toContain('accessibilityLabel={restoreLabel(line)}');
    // Nada ali decide se o botão aparece: nem o tipo da linha, nem uma regra "pode restaurar".
    expect(ignoredRows).not.toMatch(/line\.type/);
    expect(ignoredRows).not.toMatch(/canRestore/);
    expect(ignoredRows).not.toMatch(/\? \(\s*<TouchableOpacity/);
  });

  it('a regra antiga "só a saída pode ser restaurada" não existe mais', () => {
    expect(Object.keys(review)).not.toContain('canRestoreIgnored');
    expect(screen).not.toContain('canRestoreIgnored');
  });

  it('depois de restaurar, a tela mostra o aviso da linha (o da entrada) e só quando a restauração deu certo', () => {
    const handler = screen.slice(screen.indexOf('const handleRestore = async'), screen.indexOf('const backLink'));
    expect(handler).toMatch(
      /await openFinanceApiClient\.restoreReview\(\[line\.id\]\);\s+if \(getSessionEpoch\(\) !== epoch\) return;\s+const restoredText = restoredNoticeText\(line, listsIncomes\);\s+if \(restoredText\) setNotice\(\{ text: restoredText, error: false \}\);\s+\} catch/,
    );
  });
});

describe('a tela: os botões de possível duplicada', () => {
  const screen = fs.readFileSync(path.resolve(__dirname, '../../../../app/(main)/openfinance/review.tsx'), 'utf8');
  const answers = screen.slice(screen.indexOf('{line.match ? ('), screen.indexOf('<View style={styles.lineActions}>'));

  it('o trecho dos botões foi encontrado no código da tela', () => {
    expect(screen.indexOf('{line.match ? (')).toBeGreaterThan(0);
    expect(answers.length).toBeGreaterThan(100);
  });

  it('"É a mesma" chama a resposta que concilia e "É outra" a que confirma como nova, cada rótulo no seu botão', () => {
    const same = /handleAnswer\('same', line\)[\s\S]*?<Text style=\{styles\.answerText\}>([^<]+)<\/Text>/.exec(answers);
    const other = /handleAnswer\('other', line\)[\s\S]*?<Text style=\{styles\.answerText\}>([^<]+)<\/Text>/.exec(answers);
    expect(same?.[1]).toBe('É a mesma');
    expect(other?.[1]).toBe('É outra');
    // Cada botão tem uma só chamada, e a descrição falada de cada um diz o que ele faz.
    expect(answers.match(/handleAnswer\(/g)).toHaveLength(2);
    expect(answers).toMatch(/handleAnswer\('same', line\)[\s\S]*?accessibilityLabel=\{`É a mesma: [^`]*não registrar de novo`\}/);
    expect(answers).toMatch(/handleAnswer\('other', line\)[\s\S]*?accessibilityLabel=\{`É outra: registrar [^`]*como uma despesa nova`\}/);
  });

  it('a resposta vira o corpo certo: "same" concilia (reconcile), "other" confirma como nova (asNew)', () => {
    const handler = screen.slice(screen.indexOf('const handleAnswer = async'), screen.indexOf('const handleRestore = async'));
    expect(handler).toContain('answerDuplicateRequest(answer, line, chosen)');
    expect(handler).toContain("answer === 'same' ? ANSWER_SAME_FAILED_TEXT : ANSWER_OTHER_FAILED_TEXT");
    expect(answerDuplicateRequest('same', duplicate, {})).toHaveProperty('reconcile');
    expect(answerDuplicateRequest('other', duplicate, {})).toHaveProperty('asNew');
  });

  it('sessão trocada no meio da resposta: nada é mostrado nem recarregado, no aviso, no erro e no fim (época da sessão)', () => {
    const handler = screen.slice(screen.indexOf('const handleAnswer = async'), screen.indexOf('const handleRestore = async'));
    // A época é lida antes do pedido, e cada caminho depois dele confere a mesma época antes de mexer na tela.
    expect(handler).toMatch(/const epoch = getSessionEpoch\(\);\s+setBusy\('answer'\);/);
    expect(handler).toMatch(
      /confirmReview\(answerDuplicateRequest\(answer, line, chosen\)\)\)\.data;\s+if \(getSessionEpoch\(\) !== epoch\) return;\s+setNotice\(\{ text: answerDuplicateNotice\(/,
    );
    expect(handler).toMatch(/\} catch \(err\) \{\s+if \(getSessionEpoch\(\) !== epoch\) return;\s+setNotice\(/);
    expect(handler).toMatch(/finally \{\s+if \(getSessionEpoch\(\) === epoch\) \{\s+setBusy\(null\);\s+(?:\/\/[^\r\n]*[\r\n]+\s*)*invalidateEverything\(\);\s+\}\s+\}/);
    expect(handler.match(/getSessionEpoch\(\)/g)).toHaveLength(4);
  });

  it('o aviso depois da resposta vem de answerDuplicateNotice (com a categoria não usada)', () => {
    const handler = screen.slice(screen.indexOf('const handleAnswer = async'), screen.indexOf('const handleRestore = async'));
    expect(handler).toContain('answerDuplicateNotice(answer, line, chosen, result)');
    expect(handler).not.toContain('answerDuplicateResultMessage(');
  });

  it('a linha com possível duplicada fica sem caixa de seleção, e as sem marca têm', () => {
    const rows = screen.slice(screen.indexOf('renderItem={({ item: line }) => {'));
    expect(rows).toContain('const selectable = isSelectable(line);');
    expect(rows).toMatch(/\{selectable \? \(\s*<TouchableOpacity\s+style=\{styles\.checkTouch\}[\s\S]*?accessibilityRole="checkbox"/);
    // Sem caixa: só o espaço, para as linhas alinharem.
    expect(rows).toMatch(/\) : \(\s*(?:\/\/[^\r\n]*[\r\n]+\s*)*<View style=\{styles\.checkTouch\} \/>\s*\)\}/);
    // Quem decide é a função pura, e ela recusa a linha com possível duplicada.
    expect(isSelectable(duplicate)).toBe(false);
    expect(isSelectable(plain)).toBe(true);
  });

  it('o aviso do topo vem de duplicateNoticeText, com a contagem das possíveis duplicadas que já podem ser respondidas', () => {
    expect(screen).toContain('const duplicatesNotice = duplicateNoticeText(answerableDuplicateCount(expenses));');
    expect(screen).not.toContain('suspectedDuplicateCount(');
    expect(screen).toMatch(/\{duplicatesNotice \? \(\s*<View style=\{styles\.duplicatesNotice\}[\s\S]*?\{duplicatesNotice\}/);
    expect(duplicateNoticeText(answerableDuplicateCount([plain, duplicate, otherPlain]))).toBe('1 possível duplicada precisa de você');
    expect(duplicateNoticeText(answerableDuplicateCount([plain, otherPlain]))).toBeNull();
  });

  it('a revisão abre no mês das ignoradas quando nada espera (as duas listas vão para initialReviewMonth)', () => {
    expect(screen).toContain('initialReviewMonth(mergePendingByMonth(data.pendingByMonth, data.pendingIncomesByMonth), data.month, data.ignoredByMonth),');
  });
});

// Revisão 13: o servidor confere de novo na hora de confirmar. A linha que passou a parecer uma compra já registrada
// não é confirmada: volta na resposta em `heldAsPossibleDuplicates` e fica na revisão, marcada, esperando a resposta.
describe('linhas seguradas na hora de confirmar (possível duplicada descoberta pelo servidor)', () => {
  const batch = (...ids: string[]): ConfirmBankReviewRequest => ({ expenses: ids.map((id) => ({ id })), holdPossibleDuplicates: true });
  const createdFor = (...ids: string[]) => ids.map((id) => ({ id, transactionId: `t-${id}` }));

  it('o aviso, no singular e no plural; sem nenhuma, sem aviso', () => {
    expect(heldDuplicatesText(0)).toBeNull();
    expect(heldDuplicatesText(1)).toBe(
      '1 lançamento parece uma compra que você já registrou. Responda nele: é a mesma ou é outra?',
    );
    expect(heldDuplicatesText(2)).toBe(
      '2 lançamentos parecem compras que você já registrou. Responda em cada um: é a mesma ou é outra?',
    );
  });

  it('"Confirmar selecionadas" avisa o servidor, em todo lote, que ninguém respondeu sobre duplicadas', () => {
    const many = Array.from({ length: 5 }, (_, i) => line({ id: `m-${i}`, amount: 10 + i }));
    const batches = buildConfirmBatches(many, new Set(many.map((l) => l.id)), {}, 2);
    expect(batches).toHaveLength(3);
    expect(batches.every((b) => b.holdPossibleDuplicates === true)).toBe(true);
  });

  it('"É outra" e "É a mesma" são a resposta da pessoa: nunca pedem para segurar', () => {
    expect(answerDuplicateRequest('other', duplicate, {})).toEqual({ asNew: ['d'] });
    expect(answerDuplicateRequest('other', duplicate, { d: 'LAZER' })).toEqual({ expenses: [{ id: 'd', category: 'LAZER' }] });
    expect(answerDuplicateRequest('same', duplicate, {})).toEqual({ reconcile: [{ id: 'd' }] });
  });

  it('as seguradas não contam como registradas: o aviso diz quantas entraram e quantas esperam a resposta', async () => {
    const outcome = await confirmInBatches([batch('a', 'b', 'x', 'y')], {
      confirm: async () => response({ created: createdFor('a', 'b'), heldAsPossibleDuplicates: ['x', 'y'] }),
      isCurrent: () => true,
    });

    expect(outcome).toEqual({
      kind: 'done',
      text:
        '2 despesas registradas. 2 lançamentos parecem compras que você já registrou. Responda em cada um: é a mesma ou é outra?',
      heldIds: ['x', 'y'],
    });
  });

  it('soma as seguradas de todos os lotes', async () => {
    const answers = [
      response({ created: createdFor('a'), heldAsPossibleDuplicates: ['x'] }),
      response({ created: createdFor('b'), heldAsPossibleDuplicates: ['y', 'z'] }),
    ];
    let calls = 0;

    const outcome = await confirmInBatches([batch('a', 'x'), batch('b', 'y', 'z')], {
      confirm: async () => answers[calls++],
      isCurrent: () => true,
    });

    expect(outcome).toEqual({
      kind: 'done',
      text:
        '2 despesas registradas. 3 lançamentos parecem compras que você já registrou. Responda em cada um: é a mesma ou é outra?',
      heldIds: ['x', 'y', 'z'],
    });
  });

  it('todas seguradas: só o aviso delas, sem "Nada a registrar."', async () => {
    const outcome = await confirmInBatches([batch('x')], {
      confirm: async () => response({ heldAsPossibleDuplicates: ['x'] }),
      isCurrent: () => true,
    });

    expect(outcome).toEqual({
      kind: 'done',
      text: '1 lançamento parece uma compra que você já registrou. Responda nele: é a mesma ou é outra?',
      heldIds: ['x'],
    });
  });

  it('servidor que ainda não confere (sem o campo): o aviso de sempre, nada segurado', async () => {
    const old: ConfirmBankReviewResponse = { created: createdFor('a'), discarded: [], alreadyConfirmed: 0 };

    const outcome = await confirmInBatches([batch('a')], { confirm: async () => old, isCurrent: () => true });

    expect(outcome).toEqual({ kind: 'done', text: '1 despesa registrada.' });
    expect(outcome).not.toHaveProperty('heldIds');
  });

  it('erro num lote depois de um que segurou linhas: diz o que entrou, o que espera resposta e o motivo', async () => {
    let calls = 0;
    const failing = async () => {
      calls += 1;
      if (calls === 2) throw { response: { status: 500 } };
      return response({ created: createdFor('a'), heldAsPossibleDuplicates: ['x'] });
    };

    expect(await confirmInBatches([batch('a', 'x'), batch('b')], { confirm: failing, isCurrent: () => true })).toEqual({
      kind: 'failed',
      text:
        '1 despesa registrada. 1 lançamento parece uma compra que você já registrou. Responda nele: é a mesma ou é outra? O restante não entrou: Servidor com problemas. Tente novamente mais tarde.',
      heldIds: ['x'],
    });

    // Só seguradas antes do erro: nada entrou, mas elas esperam a resposta do mesmo jeito.
    calls = 0;
    const onlyHeld = async () => {
      calls += 1;
      if (calls === 2) throw { response: { status: 500 } };
      return response({ heldAsPossibleDuplicates: ['x'] });
    };
    expect(await confirmInBatches([batch('x'), batch('b')], { confirm: onlyHeld, isCurrent: () => true })).toEqual({
      kind: 'failed',
      text:
        '1 lançamento parece uma compra que você já registrou. Responda nele: é a mesma ou é outra? O restante não entrou: Servidor com problemas. Tente novamente mais tarde.',
      heldIds: ['x'],
    });
  });

  it('sessão trocada enquanto o servidor respondia: nada a mostrar, nem as seguradas', async () => {
    let current = true;

    const outcome = await confirmInBatches([batch('x')], {
      confirm: async () => {
        current = false;
        return response({ heldAsPossibleDuplicates: ['x'] });
      },
      isCurrent: () => current,
    });

    expect(outcome).toEqual({ kind: 'stale' });
  });

  it('a categoria escolhida numa linha segurada continua escolhida; as das linhas que entraram saem', () => {
    expect(chosenOfHeld({ a: 'LAZER', x: 'MERCADO', y: 'SAUDE' }, ['x', 'z'])).toEqual({ x: 'MERCADO' });
    expect(chosenOfHeld({ a: 'LAZER' }, [])).toEqual({});
    expect(chosenOfHeld({ a: 'LAZER' }, undefined)).toEqual({});
  });

  it('"É outra" com categoria numa linha que o servidor segurou de novo: o aviso é o das seguradas, não "Nada a registrar."', () => {
    expect(answerDuplicateResultMessage('other', response({ heldAsPossibleDuplicates: ['d'] }))).toBe(
      '1 lançamento parece uma compra que você já registrou. Responda nele: é a mesma ou é outra?',
    );
    expect(answerDuplicateResultMessage('other', response({ created: createdFor('d') }))).toBe('1 despesa registrada.');
  });
});

describe('a tela: "Confirmar selecionadas" com linhas seguradas', () => {
  const screen = fs.readFileSync(path.resolve(__dirname, '../../../../app/(main)/openfinance/review.tsx'), 'utf8');
  const handler = screen.slice(screen.indexOf('const handleConfirm = async'), screen.indexOf('const handleDiscard = async'));

  it('o trecho foi encontrado no código da tela', () => {
    expect(handler.length).toBeGreaterThan(200);
  });

  it('os lotes vêm de buildConfirmBatches (que pede para segurar) e o aviso de confirmInBatches (que conta as seguradas à parte)', () => {
    expect(handler).toContain('const batches = buildConfirmBatches(expenses, selected, chosen);');
    expect(handler).toContain('const outcome = await confirmInBatches(batches, {');
    expect(handler).toContain("setNotice({ text: outcome.text, error: outcome.kind === 'failed' });");
  });

  it('nada é mostrado nem alterado se a sessão mudou; senão a seleção sai e só a categoria das seguradas fica', () => {
    expect(handler).toContain('const epoch = getSessionEpoch();');
    expect(handler).toContain('isCurrent: () => getSessionEpoch() === epoch,');
    expect(handler).toMatch(
      /if \(outcome\.kind === 'stale'\) return;\s+if \(outcome\.kind === 'done'\) \{\s+setSelected\(new Set\(\)\);\s+setChosen\(\(current\) => chosenOfHeld\(current, outcome\.heldIds\)\);\s+\}/,
    );
  });

  it('a lista é recarregada depois, só na mesma sessão: as seguradas voltam marcadas', () => {
    expect(handler).toMatch(/finally \{\s+if \(getSessionEpoch\(\) === epoch\) \{\s+setBusy\(null\);\s+invalidateEverything\(\);\s+\}\s+\}/);
    expect(screen).toMatch(/const invalidateEverything = \(\) => \{\s+void queryClient\.invalidateQueries\(\{ queryKey: BANK_REVIEW_KEY \}\);/);
  });
});
