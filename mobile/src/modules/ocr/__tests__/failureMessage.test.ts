import { ocrFailureMessage } from '../failureMessage';

describe('mensagens de falha da importação', () => {
  it('limite de páginas do PDF', () => {
    expect(ocrFailureMessage('PDF_TOO_MANY_PAGES')).toBe(
      'O PDF tem páginas demais (o limite é 50). Envie apenas o período que deseja importar.',
    );
  });

  it('PDF_TIMEOUT: não afirma a causa, só sugere período menor se for grande', () => {
    expect(ocrFailureMessage('PDF_TIMEOUT')).toBe(
      'A leitura do extrato demorou demais e foi interrompida. Se ele for grande, exporte um período menor (por exemplo, um mês) e envie de novo.',
    );
  });

  it('PDF_UNREADABLE: PDF que não abre, pede para baixar de novo no banco', () => {
    expect(ocrFailureMessage('PDF_UNREADABLE')).toBe(
      'Não conseguimos abrir este PDF. Baixe o extrato de novo no app ou no site do banco e envie.',
    );
  });

  it('PDF_WORKER_FAILED: não promete que repetir resolve, sugere período menor', () => {
    expect(ocrFailureMessage('PDF_WORKER_FAILED')).toBe(
      'Não conseguimos ler este extrato. Se ele for grande, exporte um período menor (por exemplo, um mês) e envie de novo.',
    );
  });

  it('PROCESSING_TIMEOUT: leitura interrompida (não é tamanho), tem texto próprio', () => {
    const text = ocrFailureMessage('PROCESSING_TIMEOUT');
    expect(text).toBe(
      'A leitura foi interrompida antes de terminar. Envie o arquivo de novo; se acontecer outra vez, exporte um período menor (por exemplo, um mês).',
    );
    expect(text).not.toBe(ocrFailureMessage('PDF_TIMEOUT'));
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
    expect(ocrFailureMessage(null)).toBe('Falha no processamento. Tente novamente.');
  });
});
