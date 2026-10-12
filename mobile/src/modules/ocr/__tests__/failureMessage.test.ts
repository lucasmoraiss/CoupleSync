import { JOB_FAILURE_ACTION_LABEL, ocrFailureMessage, uploadErrorRetryLabel } from '../failureMessage';

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

  it('processing_error: o servidor já tentou de novo sozinho, então não manda repetir; sugere exportar de novo ou cadastrar à mão', () => {
    const text = ocrFailureMessage('processing_error');
    expect(text).toBe(
      'Não conseguimos ler este extrato. Tente exportá-lo de novo no app ou no site do banco; se continuar, cadastre as transações manualmente.',
    );
    expect(text).not.toBe(ocrFailureMessage(undefined));
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
    expect(ocrFailureMessage('codigo_que_o_app_nao_conhece')).toBe('Falha no processamento. Tente novamente.');
    expect(ocrFailureMessage(undefined)).toBe('Falha no processamento. Tente novamente.');
    expect(ocrFailureMessage(null)).toBe('Falha no processamento. Tente novamente.');
  });
});

// Um erro do ENVIO só leva o rótulo "Escolher arquivo" quando repetir o mesmo arquivo não resolve;
// erro de rede ou do servidor segue com "Tentar novamente" (o padrão do ErrorState = undefined).
const apiError = (status: number, code?: string) => ({
  response: { status, data: code ? { code, message: 'texto da API', traceId: 't' } : undefined },
});

describe('rótulo do botão após erro no envio', () => {
  it.each([
    [413, 'FILE_TOO_LARGE'],
    [415, 'UNSUPPORTED_FILE_TYPE'],
    [409, 'IMPORT_QUEUE_FULL'],
    [400, 'FILE_REQUIRED'],
  ])('%i %s: repetir o mesmo arquivo não resolve, o botão manda escolher arquivo', (status, code) => {
    expect(uploadErrorRetryLabel(apiError(status, code))).toBe(JOB_FAILURE_ACTION_LABEL);
  });

  it('413 sem corpo no formato da API (proxy): também é o arquivo', () => {
    expect(uploadErrorRetryLabel(apiError(413))).toBe(JOB_FAILURE_ACTION_LABEL);
  });

  it('erro de rede, tempo esgotado, servidor e sessão seguem com "Tentar novamente"', () => {
    expect(uploadErrorRetryLabel({ code: 'ERR_NETWORK', message: 'Network Error' })).toBeUndefined();
    expect(uploadErrorRetryLabel({ code: 'ECONNABORTED', message: 'timeout of 30000ms exceeded' })).toBeUndefined();
    expect(uploadErrorRetryLabel(apiError(500, 'INTERNAL_ERROR'))).toBeUndefined();
    expect(uploadErrorRetryLabel(apiError(503))).toBeUndefined();
    expect(uploadErrorRetryLabel(apiError(401, 'UNAUTHORIZED'))).toBeUndefined();
    expect(uploadErrorRetryLabel(apiError(429, 'RATE_LIMITED'))).toBeUndefined();
    expect(uploadErrorRetryLabel(undefined)).toBeUndefined();
    expect(uploadErrorRetryLabel(new Error('qualquer'))).toBeUndefined();
  });
});
