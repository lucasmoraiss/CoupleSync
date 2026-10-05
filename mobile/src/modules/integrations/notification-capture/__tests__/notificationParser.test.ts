import { classifyNotification, isSupportedBank } from '../notificationParser';

// Textos de notificação inventados, plausíveis; nenhum é cópia de notificação real.
const TS = Date.UTC(2026, 9, 5, 14, 30, 0);

const BANKS = [
  { bank: 'Nubank', pkg: 'com.nu.production' },
  { bank: 'Itaú', pkg: 'com.itau' },
  { bank: 'Inter', pkg: 'br.com.intermedium' },
  { bank: 'C6 Bank', pkg: 'com.c6bank.app' },
  { bank: 'Bradesco', pkg: 'com.bradesco' },
] as const;

function classify(pkg: string, title: string, body: string) {
  return classifyNotification(pkg, title, body, TS);
}

function expectUpload(pkg: string, title: string, body: string) {
  const decision = classify(pkg, title, body);
  if (decision.action !== 'upload') {
    throw new Error(`esperava "upload", veio "ignore" (${decision.reason}) para: ${title} | ${body}`);
  }
  return decision.event;
}

describe('A06 — compras e pagamentos reconhecidos (um banco por vez)', () => {
  it('Nubank: compra no crédito', () => {
    const event = expectUpload('com.nu.production', 'Compra aprovada', 'Compra no crédito: R$ 45,90 em PADARIA DO ZE');
    expect(event).toMatchObject({ bank: 'Nubank', amount: 45.9, merchant: 'PADARIA DO ZE' });
    expect(event.receivedAt).toBe('2026-10-05T14:30:00.000Z');
  });

  it('Nubank: Pix enviado', () => {
    const event = expectUpload('com.nu.production', 'Pix enviado', 'Pix enviado: R$ 150,00 para Mercearia Boa Vista.');
    expect(event).toMatchObject({ bank: 'Nubank', amount: 150, merchant: 'Mercearia Boa Vista' });
  });

  it('Itaú: compra no cartão com valor na casa do milhar', () => {
    const event = expectUpload('com.itau', 'Itaú', 'Compra no cartão: R$ 1.234,56 - MAGAZINE LUIZA');
    expect(event).toMatchObject({ bank: 'Itaú', amount: 1234.56, merchant: 'MAGAZINE LUIZA' });
  });

  it('Inter: compra aprovada', () => {
    const event = expectUpload('br.com.intermedium', 'Inter', 'Compra aprovada: R$ 89,90 em POSTO IPIRANGA');
    expect(event).toMatchObject({ bank: 'Inter', amount: 89.9, merchant: 'POSTO IPIRANGA' });
  });

  it('Inter: "Você gastou"', () => {
    const event = expectUpload('br.com.intermedium', 'Inter', 'Você gastou R$ 32,50 no Inter em UBER TRIP.');
    expect(event).toMatchObject({ bank: 'Inter', amount: 32.5, merchant: 'UBER TRIP' });
  });

  it('C6 Bank: compra aprovada', () => {
    const event = expectUpload('com.c6bank.app', 'C6 Bank', 'Compra de R$ 250,00 em AMAZON BR aprovada');
    expect(event).toMatchObject({ bank: 'C6 Bank', amount: 250, merchant: 'AMAZON BR' });
  });

  it('Bradesco: compra', () => {
    const event = expectUpload('com.bradesco', 'Bradesco', 'Compra de R$ 67,30 em DROGARIA SAO PAULO');
    expect(event).toMatchObject({ bank: 'Bradesco', amount: 67.3, merchant: 'DROGARIA SAO PAULO' });
  });

  it('Bradesco: débito em conta sem estabelecimento', () => {
    const event = expectUpload('com.bradesco', 'Bradesco', 'Débito de R$ 120,00 em sua conta');
    expect(event).toMatchObject({ bank: 'Bradesco', amount: 120, merchant: null });
  });

  it('estabelecimento chamado "DEPOSITO ..." continua sendo compra (não é confundido com depósito recebido)', () => {
    const event = expectUpload('com.bradesco', 'Bradesco', 'Compra de R$ 38,00 em DEPOSITO DE BEBIDAS SILVA');
    expect(event).toMatchObject({ amount: 38, merchant: 'DEPOSITO DE BEBIDAS SILVA' });
  });

  it('o estabelecimento não carrega o resto da frase (limite disponível)', () => {
    const event = expectUpload(
      'br.com.intermedium',
      'Inter',
      'Compra aprovada: R$ 50,00 em PADARIA PAO QUENTE. Limite disponível R$ 950,00',
    );
    expect(event).toMatchObject({ amount: 50, merchant: 'PADARIA PAO QUENTE' });
  });
});

describe('A06 — notificações que NÃO podem virar despesa', () => {
  const NEGATIVE_CASES: ReadonlyArray<readonly [string, string, string]> = [
    ['Pix recebido', 'Pix recebido', 'Você recebeu um Pix de R$ 800,00 de João Pereira.'],
    ['Pix recebido (frase curta)', 'Pix', 'Pix recebido de R$ 800,00'],
    ['transferência recebida', 'Transferência recebida', 'Transferência recebida: R$ 300,00 de Ana Lima'],
    ['depósito em conta', 'Depósito', 'Depósito de R$ 2.000,00 realizado em sua conta'],
    ['estorno de compra', 'Estorno', 'Estorno de compra: R$ 59,90 em LOJA XYZ'],
    ['fatura que vence', 'Fatura', 'Sua fatura de R$ 1.234,56 vence amanhã'],
    ['limite disponível', 'Limite', 'Limite disponível R$ 5.000,00'],
    ['propaganda com valor', 'Oferta', 'Parcele sua compra de R$ 500,00 em até 12x sem juros. Aproveite!'],
    ['propaganda de empréstimo', 'Crédito', 'Você tem R$ 10.000,00 pré-aprovado. Simule agora.'],
    ['compra negada', 'Compra negada', 'Compra de R$ 99,90 em LOJA XYZ foi negada'],
    ['compra não autorizada', 'Atenção', 'Compra não autorizada: R$ 99,90 em LOJA XYZ'],
  ];

  describe.each(BANKS)('$bank', ({ pkg }) => {
    it.each(NEGATIVE_CASES)('%s → não enviar', (_label, title, body) => {
      expect(classify(pkg, title, body).action).toBe('ignore');
    });
  });

  it('crédito é classificado como "credit" (e não como texto sem padrão)', () => {
    expect(classify('com.nu.production', 'Pix recebido', 'Você recebeu R$ 800,00 de João Pereira.')).toEqual({
      action: 'ignore',
      reason: 'credit',
    });
    expect(classify('br.com.intermedium', 'Inter', 'Pix de R$ 75,00 recebido de Carla Dias.')).toEqual({
      action: 'ignore',
      reason: 'credit',
    });
  });

  it('texto com valor mas sem padrão de compra é descartado como "no-match"', () => {
    expect(classify('com.itau', 'Itaú', 'Seu saldo é R$ 3.210,45')).toEqual({ action: 'ignore', reason: 'no-match' });
  });

  it('aplicativo que não é banco suportado é ignorado', () => {
    expect(isSupportedBank('com.whatsapp')).toBe(false);
    expect(classify('com.whatsapp', 'Zé', 'Compra de R$ 10,00 em PADARIA')).toEqual({
      action: 'ignore',
      reason: 'unsupported-bank',
    });
  });
});
