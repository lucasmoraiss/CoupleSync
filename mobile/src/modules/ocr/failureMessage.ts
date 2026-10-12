import { getApiErrorCode, getApiErrorStatus } from '../../services/apiError';

// Rótulo do botão da tela de falha da importação: ele só volta para a escolha do arquivo, não reenvia.
export const JOB_FAILURE_ACTION_LABEL = 'Escolher arquivo';

// Erros do ENVIO em que mandar o mesmo arquivo de novo não resolve: o arquivo (tamanho, tipo) ou o limite de
// extratos em leitura pedem outro arquivo ou esperar. Rede, tempo esgotado e erro do servidor ficam de fora.
const UPLOAD_CODES_THAT_NEED_ANOTHER_FILE = new Set([
  'FILE_TOO_LARGE',
  'UNSUPPORTED_FILE_TYPE',
  'FILE_REQUIRED',
  'IMPORT_QUEUE_FULL',
]);

/** Rótulo do botão após um erro de envio: "Escolher arquivo" quando repetir o mesmo arquivo não resolve; senão undefined (padrão "Tentar novamente"). */
export function uploadErrorRetryLabel(error: unknown): string | undefined {
  const code = getApiErrorCode(error);
  if (code !== undefined && UPLOAD_CODES_THAT_NEED_ANOTHER_FILE.has(code)) return JOB_FAILURE_ACTION_LABEL;
  // Um proxy pode recusar o arquivo grande antes da API, sem o corpo no formato dela.
  if (getApiErrorStatus(error) === 413) return JOB_FAILURE_ACTION_LABEL;
  return undefined;
}

// Mensagem (pt-BR) para o código de erro com que o job de importação terminou em Failed.
export function ocrFailureMessage(errorCode?: string | null, quotaResetDate?: string | null): string {
  switch (errorCode) {
    case 'quota_exhausted': {
      const dateStr = quotaResetDate
        ? new Date(quotaResetDate).toLocaleDateString('pt-BR', {
            day: '2-digit',
            month: '2-digit',
            year: 'numeric',
          })
        : '—';
      return `OCR indisponível este mês. Cota atingida. Tente novamente em ${dateStr}.`;
    }
    case 'PDF_ENCRYPTED':
      return 'O PDF está protegido por senha. Por enquanto, exporte o extrato sem senha e tente novamente. (Suporte a senha será adicionado em breve.)';
    case 'IMAGE_NOT_SUPPORTED':
      return 'Este arquivo não pôde ser lido como PDF. Envie o extrato bancário em PDF.';
    case 'PDF_TOO_SHORT':
      return 'O PDF parece ser uma imagem digitalizada. Envie um extrato em PDF digital (texto selecionável).';
    case 'NO_TRANSACTIONS_FOUND':
      return 'Nenhuma transação encontrada. Verifique se o PDF é um extrato bancário válido.';
    case 'BANK_FORMAT_UNKNOWN':
      return 'Formato do banco não reconhecido. Tente um extrato de outro banco ou cadastre as transações manualmente.';
    case 'PDF_TOO_MANY_PAGES':
      return 'O PDF tem páginas demais (o limite é 50). Envie apenas o período que deseja importar.';
    case 'PDF_TIMEOUT':
      return 'A leitura do extrato demorou demais e foi interrompida. Se ele for grande, exporte um período menor (por exemplo, um mês) e envie de novo.';
    case 'PDF_UNREADABLE':
      return 'Não conseguimos abrir este PDF. Baixe o extrato de novo no app ou no site do banco e envie.';
    case 'PDF_WORKER_FAILED':
      // O servidor não distingue falta de memória de outras mortes do processo de leitura: o texto cobre os dois casos.
      return 'Não conseguimos ler este extrato. Se ele for grande, exporte um período menor (por exemplo, um mês) e envie de novo.';
    case 'PROCESSING_TIMEOUT':
      // Job abandonado em Processing (reinício/deploy no meio): na subida da API o limite é zero; na leitura do status, 10 min. Não é culpa do tamanho do arquivo.
      return 'A leitura foi interrompida antes de terminar. Envie o arquivo de novo; se acontecer outra vez, exporte um período menor (por exemplo, um mês).';
    case 'processing_error':
      // Exceção não prevista depois das tentativas automáticas do servidor: repetir tende a não resolver.
      return 'Não conseguimos ler este extrato. Tente exportá-lo de novo no app ou no site do banco; se continuar, cadastre as transações manualmente.';
    default:
      return 'Falha no processamento. Tente novamente.';
  }
}
