// Open Finance (issue #26): regras puras da conciliação na tela "Revisão do banco": possíveis duplicadas
// ("É a mesma" / "É outra") e a seção Ignoradas. Tudo aqui é inventado; nenhum dado bancário real.
// O Jest daqui não renderiza React Native: o que é da tela (quem tem o botão "restaurar") é conferido lendo o
// código dela, como nos testes de navegação.
import * as fs from 'fs';
import * as path from 'path';
import * as review from '../review';
import {
  allSelected,
  answerDuplicateRequest,
  answerDuplicateResultMessage,
  autoReasonText,
  buildConfirmBatches,
  canAnswerDuplicate,
  duplicateNoticeText,
  hasSuspectedDuplicate,
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
import type { BankReviewLineResponse, BankReviewMatchResponse, ConfirmBankReviewResponse } from '@/types/api';

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
    expect(buildConfirmBatches(lines, selected, {})).toEqual([{ expenses: [{ id: 'a' }, { id: 'b' }] }]);
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
      /await openFinanceApiClient\.restoreReview\(\[line\.id\]\);\s+if \(getSessionEpoch\(\) !== epoch\) return;\s+const restoredText = restoredNoticeText\(line\);\s+if \(restoredText\) setNotice\(\{ text: restoredText, error: false \}\);\s+\} catch/,
    );
  });
});
