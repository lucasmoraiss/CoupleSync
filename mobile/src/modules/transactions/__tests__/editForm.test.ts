import {
  buildEditSubmission,
  createEditForm,
  formBelongsTo,
  formFor,
  originalFromParams,
  type EditRouteParams,
} from '../editForm';

// Duas transações diferentes, como a lista as manda pela rota.
const MARKET: EditRouteParams = {
  id: 'tx-a',
  amount: '42.9',
  description: 'Mercado',
  merchant: 'Pão de Açúcar',
  category: 'ALIMENTACAO',
  eventTimestampUtc: '2026-10-03T13:00:00Z', // 10:00 em Brasília
};
const RENT: EditRouteParams = {
  id: 'tx-b',
  amount: '1500',
  description: 'Aluguel',
  merchant: 'Imobiliária',
  category: 'MORADIA',
  eventTimestampUtc: '2026-10-05T15:30:00Z', // 12:30 em Brasília
};

describe('formulário de edição preso à transação (M-C1)', () => {
  it('preenche os campos com os valores da transação', () => {
    const form = createEditForm(originalFromParams(MARKET));

    expect(form).toMatchObject({
      transactionId: 'tx-a',
      amountText: '42,90',
      merchant: 'Pão de Açúcar',
      description: 'Mercado',
      dateText: '03/10/2026',
      timeText: '10:00',
      category: 'ALIMENTACAO',
    });
  });

  it('o defeito: campos da transação A com a tela mostrando a B — nada é enviado para a B', () => {
    // A tela (aba oculta) ficou montada: o estado é o da primeira visita, os parâmetros já são os da segunda.
    const formOfA = createEditForm(originalFromParams(MARKET));
    const nowShowing = originalFromParams(RENT);

    const submission = buildEditSubmission(formOfA, nowShowing);

    expect(submission).toEqual({ kind: 'stale' });
    expect(formBelongsTo(formOfA, nowShowing)).toBe(false);
  });

  it('formFor troca o formulário velho por um novo, da transação que está na tela', () => {
    const formOfA = { ...createEditForm(originalFromParams(MARKET)), amountText: '99,99' };

    const form = formFor(formOfA, originalFromParams(RENT));

    expect(form.transactionId).toBe('tx-b');
    expect(form.amountText).toBe('1500,00');
    expect(form.description).toBe('Aluguel');
    // E o formulário certo é mantido como está (as edições em andamento não se perdem a cada render).
    const editing = { ...createEditForm(originalFromParams(RENT)), description: 'Aluguel de outubro' };
    expect(formFor(editing, originalFromParams(RENT))).toBe(editing);
  });

  it('a mesma transação reaberta com valores novos (já editada) também invalida o formulário antigo', () => {
    const before = createEditForm(originalFromParams(MARKET));
    const afterEdit = originalFromParams({ ...MARKET, amount: '50' });

    expect(buildEditSubmission(before, afterEdit)).toEqual({ kind: 'stale' });
    expect(formFor(before, afterEdit).amountText).toBe('50,00');
  });

  it('sem id não há o que salvar', () => {
    const original = originalFromParams({ ...MARKET, id: undefined });
    expect(buildEditSubmission(createEditForm(original), original)).toEqual({ kind: 'stale' });
  });
});

describe('o que é enviado ao salvar', () => {
  const original = originalFromParams(MARKET);
  const form = createEditForm(original);

  it('nada mudou: não chama a API', () => {
    expect(buildEditSubmission(form, original)).toEqual({ kind: 'unchanged' });
  });

  it('envia só os campos alterados, para o id do próprio formulário', () => {
    const submission = buildEditSubmission({ ...form, amountText: '45,00', category: 'LAZER' }, original);

    expect(submission).toEqual({ kind: 'update', id: 'tx-a', body: { amount: 45, category: 'LAZER' } });
  });

  it('descrição e estabelecimento: compara sem espaços nas pontas e envia aparado; vazio limpa', () => {
    expect(buildEditSubmission({ ...form, description: '  Mercado  ' }, original)).toEqual({ kind: 'unchanged' });
    expect(buildEditSubmission({ ...form, description: ' Feira ', merchant: '' }, original)).toEqual({
      kind: 'update',
      id: 'tx-a',
      body: { description: 'Feira', merchant: '' },
    });
  });

  it('data ou hora alterada vira o instante em UTC (horário de Brasília)', () => {
    const submission = buildEditSubmission({ ...form, dateText: '04/10/2026', timeText: '08:15' }, original);

    expect(submission).toEqual({ kind: 'update', id: 'tx-a', body: { eventTimestampUtc: '2026-10-04T11:15:00.000Z' } });
  });

  it('valor inválido e data inválida param antes de qualquer envio, com a mensagem do campo', () => {
    expect(buildEditSubmission({ ...form, amountText: 'abc' }, original)).toMatchObject({ kind: 'invalid', title: 'Valor inválido' });
    expect(buildEditSubmission({ ...form, dateText: '31/02/2026' }, original)).toMatchObject({ kind: 'invalid', title: 'Data inválida' });
  });

  it('categoria vinda com acento ou em minúsculas é a chave canônica (não conta como alteração)', () => {
    const accented = originalFromParams({ ...MARKET, category: 'Alimentação' });
    expect(accented.category).toBe('ALIMENTACAO');
    expect(buildEditSubmission(createEditForm(accented), accented)).toEqual({ kind: 'unchanged' });
  });
});
