import type { OcrCandidateResponse } from '@/types/api';
import {
  buildDiscardRestRequest,
  buildOcrConfirmRequest,
  candidateToRow,
  creditsLabel,
  finishConfirmMessage,
  formatBRLInput,
  openImportSummary,
  parseBRLInput,
  isReviewLoading,
  seedReviewRows,
  sessionFor,
  shouldSeedReview,
  validateReviewRows,
  unselectedCount,
  type ReviewRow,
} from '../confirmRequest';

function candidate(index: number, description: string, amount: number): OcrCandidateResponse {
  return {
    index,
    date: '2026-09-10T00:00:00Z',
    description,
    amount,
    currency: 'BRL',
    confidence: 0.9,
    duplicateSuspected: false,
  };
}

const CANDIDATES: readonly OcrCandidateResponse[] = [
  candidate(0, 'MERCADO EXTRA', 152.3),
  candidate(1, 'UBER *TRIP', 23.9),
  candidate(2, 'PAG*PADARIA', 18.5),
];

/** Linhas como a tela as monta, com as alterações do usuário aplicadas por índice. */
function rowsWith(changes: Record<number, Partial<ReviewRow>> = {}): ReviewRow[] {
  return CANDIDATES.map(candidateToRow).map((row) => ({ ...row, ...changes[row.index] }));
}

describe('A04 — corpo da confirmação do extrato (candidateEdits)', () => {
  it('linha sem alteração não gera edit', () => {
    const request = buildOcrConfirmRequest(rowsWith(), CANDIDATES);

    expect(request.selectedIndices).toEqual([0, 1, 2]);
    expect(request.candidateEdits ?? []).toEqual([]);
  });

  it('alteração só de valor envia apenas amount', () => {
    const request = buildOcrConfirmRequest(rowsWith({ 1: { amountCents: 2590 } }), CANDIDATES);

    expect(request.candidateEdits).toEqual([{ index: 1, amount: 25.9 }]);
  });

  it('alteração só de descrição envia apenas description (sem espaços nas pontas)', () => {
    const request = buildOcrConfirmRequest(rowsWith({ 2: { description: '  Padaria da esquina ' } }), CANDIDATES);

    expect(request.candidateEdits).toEqual([{ index: 2, description: 'Padaria da esquina' }]);
  });

  it('alteração de descrição e valor na mesma linha envia os dois campos', () => {
    const request = buildOcrConfirmRequest(
      rowsWith({ 0: { description: 'Mercado', amountCents: 15000 } }),
      CANDIDATES,
    );

    expect(request.candidateEdits).toEqual([{ index: 0, description: 'Mercado', amount: 150 }]);
  });

  it('linha não selecionada é ignorada, mesmo editada', () => {
    const request = buildOcrConfirmRequest(
      rowsWith({ 0: { selected: false, description: 'Outra coisa', amountCents: 1 }, 1: { amountCents: 3000 } }),
      CANDIDATES,
    );

    expect(request.selectedIndices).toEqual([1, 2]);
    expect(request.candidateEdits).toEqual([{ index: 1, amount: 30 }]);
  });

  it('valor digitado em formato brasileiro (1.234,56) é convertido corretamente', () => {
    expect(parseBRLInput('1.234,56')).toBe(123456);
    expect(formatBRLInput(123456)).toBe('1.234,56');

    const request = buildOcrConfirmRequest(rowsWith({ 0: { amountCents: parseBRLInput('1.234,56') } }), CANDIDATES);

    expect(request.candidateEdits).toEqual([{ index: 0, amount: 1234.56 }]);
  });

  it('valor original com imprecisão de ponto flutuante não é tratado como alterado', () => {
    const candidates = [candidate(0, 'LOJA', 1234.56), candidate(1, 'LOJA 2', 0.1 + 0.2)];
    const request = buildOcrConfirmRequest(candidates.map(candidateToRow), candidates);

    expect(request.candidateEdits ?? []).toEqual([]);
  });

  it('mantém categoryOverrides das linhas selecionadas', () => {
    const request = buildOcrConfirmRequest(
      rowsWith({ 0: { category: ' Alimentação ' }, 1: { selected: false, category: 'Transporte' } }),
      CANDIDATES,
    );

    // A API recebe a chave canônica, qualquer que seja a grafia da linha.
    expect(request.categoryOverrides).toEqual([{ index: 0, category: 'ALIMENTACAO' }]);
  });

  it('não envia categoria fora da lista canônica nem linha sem categoria', () => {
    const request = buildOcrConfirmRequest(
      rowsWith({ 0: { category: 'Mercado' }, 1: { category: '' }, 2: { category: 'saude' } }),
      CANDIDATES,
    );

    expect(request.categoryOverrides).toEqual([{ index: 2, category: 'SAUDE' }]);
  });

  it('a sugestão do servidor entra na linha como chave canônica', () => {
    expect(candidateToRow({ ...candidate(0, 'X', 1), suggestedCategory: 'Alimentação' }).category).toBe('ALIMENTACAO');
    expect(candidateToRow({ ...candidate(0, 'X', 1), suggestedCategory: 'Educação' }).category).toBe('');
    expect(candidateToRow(candidate(0, 'X', 1)).category).toBe('');
  });
});

describe('A04 — validação das linhas antes de enviar', () => {
  it('sem alterações não há erros', () => {
    expect(validateReviewRows(rowsWith(), CANDIDATES)).toEqual({});
  });

  it('descrição apagada gera erro na linha', () => {
    expect(validateReviewRows(rowsWith({ 1: { description: '   ' } }), CANDIDATES)).toEqual({
      1: { description: 'Informe uma descrição.' },
    });
  });

  it('descrição com mais de 512 caracteres gera erro na linha', () => {
    const errors = validateReviewRows(rowsWith({ 0: { description: 'a'.repeat(513) } }), CANDIDATES);

    expect(errors).toEqual({ 0: { description: 'A descrição deve ter no máximo 512 caracteres.' } });
    expect(validateReviewRows(rowsWith({ 0: { description: 'a'.repeat(512) } }), CANDIDATES)).toEqual({});
  });

  it('valor zerado gera erro na linha', () => {
    expect(validateReviewRows(rowsWith({ 2: { amountCents: parseBRLInput('0,00') } }), CANDIDATES)).toEqual({
      2: { amount: 'Informe um valor maior que zero.' },
    });
  });

  it('valor acima de R$ 999.999.999,99 gera erro na linha, e o campo nunca passa do teto', () => {
    expect(parseBRLInput('9.999.999.999,99')).toBe(99_999_999_999);
    expect(parseBRLInput('99.999.999.999.999,99')).toBe(99_999_999_999);
    expect(validateReviewRows(rowsWith({ 0: { amountCents: 100_000_000_000 } }), CANDIDATES)).toEqual({
      0: { amount: 'O valor máximo é R$ 999.999.999,99.' },
    });
    expect(validateReviewRows(rowsWith({ 0: { amountCents: 99_999_999_999 } }), CANDIDATES)).toEqual({});
  });

  it('linha não selecionada não é validada', () => {
    expect(
      validateReviewRows(rowsWith({ 2: { selected: false, description: '', amountCents: 0 } }), CANDIDATES),
    ).toEqual({});
  });
});

describe('MOB-06 — estado da revisão acompanha o uploadId', () => {
  it('estado de outra importação não é reaproveitado', () => {
    const previous = {
      uploadId: 'upload-A',
      rows: CANDIDATES.map(candidateToRow),
      seeded: true,
    };

    const current = sessionFor(previous, 'upload-B');

    expect(current.uploadId).toBe('upload-B');
    expect(current.rows).toEqual([]);
    expect(current.seeded).toBe(false);
  });

  it('mesmo uploadId mantém as linhas e edições', () => {
    const previous = { uploadId: 'upload-A', rows: rowsWith({ 0: { description: 'Editada' } }), seeded: true };

    expect(sessionFor(previous, 'upload-A')).toBe(previous);
  });

  it('só as linhas pendentes entram na revisão', () => {
    const rows = seedReviewRows([
      { ...candidate(0, 'JA IMPORTADA', 10), lineState: 'Confirmed' },
      { ...candidate(1, 'DESCARTADA', 20), lineState: 'Discarded' },
      { ...candidate(2, 'PENDENTE', 30), lineState: 'Pending' },
      candidate(3, 'API ANTIGA SEM ESTADO', 40),
    ]);

    expect(rows.map((r) => r.index)).toEqual([2, 3]);
    expect(rows.every((r) => r.selected)).toBe(true);
  });
});

describe('S5 5.58 — entradas informadas, nunca importadas', () => {
  it('conta no singular e no plural', () => {
    expect(creditsLabel(0)).toBe('');
    expect(creditsLabel(1)).toBe('1 entrada não importada');
    expect(creditsLabel(3)).toBe('3 entradas não importadas');
  });
});

describe('confirmar e continuar depois', () => {
  it('a chamada de sempre não envia keepJobOpen (o app instalado e o botão "finalizar" fecham a importação)', () => {
    const request = buildOcrConfirmRequest(rowsWith({ 1: { selected: false } }), CANDIDATES);

    expect(request.selectedIndices).toEqual([0, 2]);
    expect('keepJobOpen' in request).toBe(false);
  });

  it('"continuar depois" envia keepJobOpen e só as linhas selecionadas', () => {
    const request = buildOcrConfirmRequest(rowsWith({ 1: { selected: false } }), CANDIDATES, { keepJobOpen: true });

    expect(request.selectedIndices).toEqual([0, 2]);
    expect(request.keepJobOpen).toBe(true);
  });

  it('conta as linhas deixadas de fora', () => {
    expect(unselectedCount(rowsWith())).toBe(0);
    expect(unselectedCount(rowsWith({ 0: { selected: false }, 2: { selected: false } }))).toBe(2);
  });

  it('o texto de finalizar avisa que o restante é descartado', () => {
    expect(finishConfirmMessage(1)).toBe('A transação não selecionada será descartada e a importação será encerrada.');
    expect(finishConfirmMessage(3)).toBe('As 3 transações não selecionadas serão descartadas e a importação será encerrada.');
  });

  it('descartar o que sobrou envia os índices pendentes e mantém a importação aberta só para fechá-la', () => {
    expect(buildDiscardRestRequest([4, 5])).toEqual({
      selectedIndices: [],
      discardedIndices: [4, 5],
      keepJobOpen: true,
    });
  });

  it('resume a importação aberta para a lista', () => {
    expect(
      openImportSummary({
        uploadId: 'a',
        fileName: 'extrato-setembro.pdf',
        createdAtUtc: '2026-09-10T15:00:00Z',
        pendingLines: 2,
        totalLines: 5,
        creditsCount: 0,
      }),
    ).toEqual({ title: 'extrato-setembro.pdf', pending: '2 de 5 transações pendentes' });
    expect(
      openImportSummary({
        uploadId: 'b',
        fileName: null,
        createdAtUtc: '2026-09-10T15:00:00Z',
        pendingLines: 1,
        totalLines: 1,
        creditsCount: 0,
      }),
    ).toEqual({ title: 'Extrato', pending: '1 de 1 transação pendente' });
  });
});

describe('M-I1 — reabrir a MESMA importação mostra as linhas de agora, não as da visita anterior', () => {
  const state = (overrides: Partial<Parameters<typeof shouldSeedReview>[0]> = {}) => ({
    hasData: true,
    isFetching: false,
    isError: false,
    seeded: false,
    ...overrides,
  });

  it('o defeito: com a resposta da visita anterior no cache e a busca nova em andamento, NÃO preenche', () => {
    const revisit = state({ hasData: true, isFetching: true });
    expect(shouldSeedReview(revisit)).toBe(false);
    expect(isReviewLoading(revisit)).toBe(true);
  });

  it('preenche quando a resposta desta visita chega', () => {
    expect(shouldSeedReview(state())).toBe(true);
    expect(isReviewLoading(state())).toBe(false);
  });

  it('preenche uma vez só: as edições do usuário não são sobrescritas por renders ou buscas posteriores', () => {
    expect(shouldSeedReview(state({ seeded: true }))).toBe(false);
    expect(shouldSeedReview(state({ seeded: true, isFetching: true }))).toBe(false);
    expect(isReviewLoading(state({ seeded: true, isFetching: true }))).toBe(false);
  });

  it('se a busca desta visita falha, a resposta antiga não é usada: a tela mostra o erro', () => {
    const failed = state({ hasData: true, isError: true });
    expect(shouldSeedReview(failed)).toBe(false);
    expect(isReviewLoading(failed)).toBe(false);
  });

  it('primeira abertura (sem nada no cache): carregando até a resposta chegar', () => {
    expect(isReviewLoading(state({ hasData: false, isFetching: true }))).toBe(true);
    expect(shouldSeedReview(state({ hasData: false, isFetching: true }))).toBe(false);
  });

  it('depois de "confirmar e continuar depois", só as linhas ainda pendentes voltam', () => {
    const lines = [candidate(0, 'A', 10), { ...candidate(1, 'B', 20), lineState: 'Confirmed' as const }, candidate(2, 'C', 30)];
    expect(seedReviewRows(lines).map((row) => row.index)).toEqual([0, 2]);
  });
});
