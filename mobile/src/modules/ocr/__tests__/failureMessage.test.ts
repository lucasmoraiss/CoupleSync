import { ocrFailureMessage } from '../failureMessage';

describe('mensagens de falha da importação', () => {
  it('limite de páginas do PDF', () => {
    expect(ocrFailureMessage('PDF_TOO_MANY_PAGES')).toBe(
      'O PDF tem páginas demais (o limite é 50). Envie apenas o período que deseja importar.',
    );
  });

  it('PDF_TIMEOUT e PROCESSING_TIMEOUT pedem para enviar de novo', () => {
    const expected = 'A leitura do extrato demorou demais e foi interrompida. Envie o arquivo novamente.';
    expect(ocrFailureMessage('PDF_TIMEOUT')).toBe(expected);
    expect(ocrFailureMessage('PROCESSING_TIMEOUT')).toBe(expected);
  });

  it('códigos que já existiam seguem com a mesma mensagem', () => {
    expect(ocrFailureMessage('PDF_TOO_SHORT')).toContain('imagem digitalizada');
    expect(ocrFailureMessage('BANK_FORMAT_UNKNOWN')).toContain('Formato do banco não reconhecido');
    expect(ocrFailureMessage('NO_TRANSACTIONS_FOUND')).toContain('Nenhuma transação encontrada');
    expect(ocrFailureMessage('PDF_ENCRYPTED')).toContain('protegido por senha');
  });

  it('cota esgotada mostra a data de renovação', () => {
    expect(ocrFailureMessage('quota_exhausted', '2026-10-30T00:00:00Z')).toContain('Cota atingida');
  });

  it('código desconhecido ou ausente cai na mensagem genérica', () => {
    expect(ocrFailureMessage('processing_error')).toBe('Falha no processamento. Tente novamente.');
    expect(ocrFailureMessage(undefined)).toBe('Falha no processamento. Tente novamente.');
  });
});
