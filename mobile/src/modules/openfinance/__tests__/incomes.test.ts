// Open Finance (issue #27): regras puras das Entradas da tela "Revisão do banco" (dinheiro que entrou e pode
// virar renda do mês). Tudo aqui é inventado; nenhum dado bancário real.
import * as fs from 'fs';
import * as path from 'path';
import {
  INCOME_NAME_MAX,
  REGISTER_INCOME_LABEL,
  allSelected,
  buildConfirmBatches,
  canRegisterIncome,
  defaultIncomeName,
  discardedLineText,
  incomeBlockedReason,
  incomeLineText,
  incomeNameError,
  incomeRequest,
  incomeResultMessage,
  initialReviewMonth,
  mergePendingByMonth,
  pendingReviewCount,
  restoredNoticeText,
  selectAllIds,
} from '../review';
import { incomeOriginText } from '../incomeOrigin';
import type { BankReviewLineResponse, BankReviewResponse, ConfirmBankReviewResponse } from '@/types/api';

const entry = (over: Partial<BankReviewLineResponse> = {}): BankReviewLineResponse => ({
  id: 'in-1',
  day: '2026-10-05',
  merchant: null,
  description: 'Pagamento Empresa Exemplo',
  amount: 3500,
  currency: 'BRL',
  suggestedCategory: 'OUTROS',
  bankStatus: 'Posted',
  bankName: 'Banco Exemplo',
  accountName: 'Conta Corrente',
  installmentNumber: null,
  installmentTotal: null,
  type: 'Credit',
  ...over,
});

const result = (over: Partial<ConfirmBankReviewResponse> = {}): ConfirmBankReviewResponse => ({
  created: [],
  discarded: [],
  alreadyConfirmed: 0,
  skipped: [],
  skippedOtherCurrency: [],
  reconciled: [],
  incomes: [],
  ...over,
});

const plain = (text: string) => text.replace(/ /g, ' ');

describe('o que pode virar renda', () => {
  it('só entrada efetivada no banco, com valor e em reais', () => {
    expect(canRegisterIncome(entry())).toBe(true);
    expect(incomeBlockedReason(entry())).toBeNull();
  });

  it('pendente no banco, sem valor ou em outra moeda: não pode, e diz por quê', () => {
    expect(canRegisterIncome(entry({ bankStatus: 'Pending' }))).toBe(false);
    expect(incomeBlockedReason(entry({ bankStatus: 'Pending' }))).toBe('Pendente no banco: poderá virar renda quando o banco efetivar.');
    expect(canRegisterIncome(entry({ amount: 0 }))).toBe(false);
    expect(incomeBlockedReason(entry({ amount: 0 }))).toBe('Sem valor: não vira renda. Você pode descartar.');
    expect(canRegisterIncome(entry({ currency: 'USD' }))).toBe(false);
    expect(incomeBlockedReason(entry({ currency: 'USD' }))).toBe('Em outra moeda: ainda não pode virar renda. Você pode descartar.');
  });

  it('o texto do botão é o da tela', () => {
    expect(REGISTER_INCOME_LABEL).toBe('Registrar como renda do mês');
  });
});

describe('o nome da renda', () => {
  it('parte da descrição; sem ela, do estabelecimento; sem nada, de um texto fixo', () => {
    expect(defaultIncomeName(entry())).toBe('Pagamento Empresa Exemplo');
    expect(defaultIncomeName(entry({ description: '   ', merchant: 'Empresa Exemplo Ltda' }))).toBe('Empresa Exemplo Ltda');
    expect(defaultIncomeName(entry({ description: null, merchant: null }))).toBe('Entrada do banco');
  });

  it('cabe no tamanho de um nome de renda', () => {
    const long = 'Transferência recebida de Empresa Exemplo de Serviços Administrativos e Participações Ltda';
    expect(defaultIncomeName(entry({ description: long }))).toBe(long.slice(0, INCOME_NAME_MAX).trim());
    expect(defaultIncomeName(entry({ description: long })).length).toBeLessThanOrEqual(64);
  });

  it('nome digitado longo demais é erro; vazio não (vale a descrição)', () => {
    expect(incomeNameError('Salário')).toBeNull();
    expect(incomeNameError('')).toBeNull();
    expect(incomeNameError('   ')).toBeNull();
    expect(incomeNameError('n'.repeat(64))).toBeNull();
    expect(incomeNameError(`  ${'n'.repeat(64)}  `)).toBeNull();
    expect(incomeNameError('n'.repeat(65))).toBe('O nome deve ter no máximo 64 caracteres.');
  });
});

describe('incomeRequest: o corpo de POST review/confirm', () => {
  it('manda só o id quando a pessoa não trocou o nome (o servidor usa a descrição)', () => {
    expect(incomeRequest(entry(), 'Pagamento Empresa Exemplo')).toEqual({ incomes: [{ id: 'in-1' }] });
    expect(incomeRequest(entry(), '  Pagamento Empresa Exemplo ')).toEqual({ incomes: [{ id: 'in-1' }] });
    expect(incomeRequest(entry(), '')).toEqual({ incomes: [{ id: 'in-1' }] });
    expect(incomeRequest(entry(), '   ')).toEqual({ incomes: [{ id: 'in-1' }] });
  });

  it('manda o nome digitado, sem espaços nas pontas', () => {
    expect(incomeRequest(entry(), '  Salário da Ana ')).toEqual({ incomes: [{ id: 'in-1', name: 'Salário da Ana' }] });
  });

  it('nunca manda valor, moeda nem a linha como despesa', () => {
    const body = incomeRequest(entry(), 'Salário');
    expect(Object.keys(body)).toEqual(['incomes']);
    expect(Object.keys(body.incomes![0]).sort()).toEqual(['id', 'name']);
  });
});

describe('incomeResultMessage', () => {
  it('registrada: diz o mês da renda (o do dia da entrada)', () => {
    const done = result({ incomes: [{ id: 'in-1', incomeSourceId: 's-1' }] });
    expect(incomeResultMessage(entry({ day: '2026-10-05' }), done)).toBe('Renda registrada em outubro de 2026.');
    expect(incomeResultMessage(entry({ day: '2026-09-30' }), done)).toBe('Renda registrada em setembro de 2026.');
  });

  it('já estava registrada, pulada por outra moeda, pulada por não ter valor, ou nada', () => {
    expect(incomeResultMessage(entry(), result({ alreadyConfirmed: 1 }))).toBe('Esta entrada já estava registrada como renda.');
    expect(incomeResultMessage(entry(), result({ skipped: ['in-1'], skippedOtherCurrency: ['in-1'] }))).toBe(
      'Em outra moeda: não foi registrada. Ela continua na revisão.',
    );
    expect(incomeResultMessage(entry(), result({ skipped: ['in-1'] }))).toBe('Sem valor: não foi registrada. Ela continua na revisão.');
    expect(incomeResultMessage(entry(), result())).toBe('Nada foi registrado.');
  });

  it('servidor que não devolve os campos novos: nada quebra', () => {
    const old = { created: [], discarded: [], alreadyConfirmed: 0 } as ConfirmBankReviewResponse;
    expect(incomeResultMessage(entry(), old)).toBe('Nada foi registrado.');
  });
});

describe('textos das linhas', () => {
  it('incomeLineText: o dia e o valor, na moeda da linha', () => {
    expect(plain(incomeLineText(entry()))).toBe('05/10/2026 · entrada de R$ 3.500,00');
    expect(plain(incomeLineText(entry({ amount: 50, currency: 'USD' })))).toBe('05/10/2026 · entrada de US$ 50,00');
  });

  it('discardedLineText: diz quando a descartada é uma entrada', () => {
    expect(plain(discardedLineText(entry({ amount: 15 })))).toBe('05/10/2026 · entrada de R$ 15,00');
    expect(plain(discardedLineText(entry({ amount: 15, type: 'Debit' })))).toBe('05/10/2026 · R$ 15,00');
    expect(plain(discardedLineText(entry({ amount: 15, type: undefined })))).toBe('05/10/2026 · R$ 15,00');
  });

  it('restoredNoticeText: com servidor que lista entradas, a entrada restaurada volta para as Entradas', () => {
    expect(restoredNoticeText({ type: 'Credit' }, true)).toBe('Entrada restaurada: ela voltou para as Entradas desta tela.');
    // Servidor anterior: a entrada some daqui (a tela só listava despesas), e o aviso continua o de antes.
    expect(restoredNoticeText({ type: 'Credit' }, false)).toBe(
      'Entrada restaurada: ela deixou de ser ignorada. Esta lista é só de despesas, por isso ela não aparece aqui.',
    );
    expect(restoredNoticeText({ type: 'Credit' })).toBe(restoredNoticeText({ type: 'Credit' }, false));
    expect(restoredNoticeText({ type: 'Debit' }, true)).toBeNull();
    expect(restoredNoticeText({ type: 'Debit' }, false)).toBeNull();
  });
});

describe('meses e contagens: entradas contam junto com as despesas', () => {
  it('mergePendingByMonth soma por mês, do mais recente para o mais antigo', () => {
    expect(
      mergePendingByMonth(
        [
          { month: '2026-10', pending: 3 },
          { month: '2026-08', pending: 1 },
        ],
        [
          { month: '2026-10', pending: 2 },
          { month: '2026-09', pending: 4 },
        ],
      ),
    ).toEqual([
      { month: '2026-10', pending: 5 },
      { month: '2026-09', pending: 4 },
      { month: '2026-08', pending: 1 },
    ]);
  });

  it('servidor anterior às entradas (lista ausente): ficam só as despesas', () => {
    expect(mergePendingByMonth([{ month: '2026-10', pending: 3 }], undefined)).toEqual([{ month: '2026-10', pending: 3 }]);
    expect(mergePendingByMonth(undefined, undefined)).toEqual([]);
    expect(mergePendingByMonth(null, [{ month: '2026-09', pending: 1 }])).toEqual([{ month: '2026-09', pending: 1 }]);
  });

  it('a revisão abre no mês mais recente com algo esperando, também quando só há entrada nele', () => {
    const merged = mergePendingByMonth([{ month: '2026-08', pending: 2 }], [{ month: '2026-09', pending: 1 }]);
    expect(initialReviewMonth(merged, '2026-10')).toBe('2026-09');
  });

  it('pendingReviewCount: despesas e entradas que podem ser registradas', () => {
    const review = { pendingAllMonths: 3, pendingIncomesAllMonths: 2 } as BankReviewResponse;
    expect(pendingReviewCount(review)).toBe(5);
    expect(pendingReviewCount({ pendingAllMonths: 3 } as BankReviewResponse)).toBe(3);
    expect(pendingReviewCount({} as BankReviewResponse)).toBe(0);
    expect(pendingReviewCount(undefined)).toBe(0);
  });
});

describe('"Selecionar tudo" continua só nas despesas', () => {
  it('uma entrada nunca é selecionável nem vai em "Confirmar selecionadas", mesmo se cair na lista', () => {
    const expense = entry({ id: 'out-1', type: 'Debit', description: 'Cantina Exemplo', amount: 58.9 });
    const income = entry({ id: 'in-1' });

    expect(selectAllIds([expense, income])).toEqual(['out-1']);
    expect(allSelected([expense, income], new Set(['out-1']))).toBe(true);
    expect(buildConfirmBatches([expense, income], new Set(['out-1', 'in-1']), {})).toEqual([{ expenses: [{ id: 'out-1' }] }]);
  });
});

describe('erro ao registrar a renda: fica à vista, na folha aberta', () => {
  const screen = fs.readFileSync(path.resolve(__dirname, '../../../../app/(main)/openfinance/review.tsx'), 'utf8');
  const handler = screen.slice(screen.indexOf('const handleRegisterIncome = async'), screen.indexOf('const handleConfirm = async'));
  const failed = handler.slice(handler.indexOf('} catch (err) {'), handler.indexOf('} finally {'));
  const sheet = screen.slice(screen.indexOf('visible={registering !== null}'), screen.indexOf('visible={pickingFor !== null}'));

  it('a falha não fecha a folha nem vai para o aviso do topo da lista: a mensagem fica na folha', () => {
    expect(handler.length).toBeGreaterThan(0);
    expect(failed).toContain('setIncomeError(getApiErrorMessage(err, REGISTER_INCOME_FAILED_TEXT));');
    expect(failed).not.toContain('setRegistering(null)');
    expect(failed).not.toContain('setNotice(');
    // Trabalho assíncrono preso à sessão: quem saiu da conta não recebe a mensagem.
    expect(failed).toContain('if (getSessionEpoch() !== epoch) return;');
  });

  it('a folha mostra a mensagem como alerta, logo acima do botão de registrar', () => {
    const message = sheet.indexOf('{incomeError ? (');
    expect(message).toBeGreaterThan(-1);
    expect(message).toBeLessThan(sheet.indexOf('accessibilityLabel="Registrar renda"'));
    expect(sheet.slice(message, message + 400)).toContain('accessibilityRole="alert"');
    expect(sheet.slice(message, message + 400)).toContain('{incomeError}');
  });

  it('a mensagem some ao abrir a folha de novo e ao tentar outra vez; o sucesso continua fechando a folha', () => {
    const open = screen.slice(screen.indexOf('const openRegisterIncome ='), screen.indexOf('const handleRegisterIncome = async'));
    expect(open).toContain('setIncomeError(null);');
    const tried = handler.slice(0, handler.indexOf('} catch (err) {'));
    expect(tried.indexOf('setIncomeError(null);')).toBeGreaterThan(-1);
    expect(tried.indexOf('setIncomeError(null);')).toBeLessThan(tried.indexOf('await openFinanceApiClient.confirmReview('));
    expect(tried).toContain('setRegistering(null);');
  });
});

describe('tela de Rendas: a origem "do banco"', () => {
  it('só a renda que veio de uma entrada do banco mostra a origem', () => {
    expect(incomeOriginText({ fromBank: true })).toBe('do banco');
    expect(incomeOriginText({ fromBank: false })).toBeNull();
    // Servidor anterior não manda o campo: nenhuma renda é marcada.
    expect(incomeOriginText({})).toBeNull();
  });

  it('a tela mostra a origem nas duas formas da linha (a que edita e a que só lê)', () => {
    const screen = fs.readFileSync(path.resolve(__dirname, '../../../../app/(main)/budget/index.tsx'), 'utf8');
    expect(screen).toContain('const origin = incomeOriginText(source);');
    expect(screen.match(/\{origin \? <Text style=\{styles\.sourceOrigin\}>\{origin\}<\/Text> : null\}/g)).toHaveLength(2);
  });
});
