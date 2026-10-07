// Textos de privacidade exibidos dentro do app (sem link externo). Um só lugar para que a tela de consentimento,
// a tela "Privacidade" e o aviso do chat não se contradigam.

import { formatConsentDate, type ConsentRecord } from './consent';

export interface TextSection {
  readonly title: string;
  readonly paragraphs: readonly string[];
}

export interface CaptureSource {
  /** Nome do banco, como aparece para o usuário. */
  readonly bank: string;
  /** Identificadores Android dos aplicativos desse banco cujas notificações são lidas. */
  readonly packages: readonly string[];
}

/**
 * TUDO o que a captura lê: os bancos e, para cada um, os aplicativos (identificador Android). É a mesma lista
 * de notification-patterns.json e do serviço nativo (SUPPORTED_PACKAGES); um teste compara as três, pacote a
 * pacote, e exige que cada identificador apareça no texto do consentimento.
 */
export const CAPTURE_SOURCES: readonly CaptureSource[] = [
  { bank: 'Nubank', packages: ['com.nu.production'] },
  { bank: 'Itaú', packages: ['com.itau', 'br.com.italiquido', 'com.itau.empresas'] },
  { bank: 'Inter', packages: ['br.com.intermedium'] },
  { bank: 'C6 Bank', packages: ['com.c6bank.app'] },
  { bank: 'Bradesco', packages: ['com.bradesco', 'com.bradesco.prestoandroid', 'com.bradesco.next'] },
];

/** Bancos cujas notificações a captura lê. */
export const CAPTURE_APPS: readonly string[] = CAPTURE_SOURCES.map((source) => source.bank);

function listInPortuguese(items: readonly string[]): string {
  if (items.length <= 1) return items.join('');
  return `${items.slice(0, -1).join(', ')} e ${items[items.length - 1]}`;
}

const BANKS_WITH_SEVERAL_APPS = CAPTURE_SOURCES.filter((source) => source.packages.length > 1).map((source) => source.bank);

/** O que é lido, por extenso: cada banco com os identificadores dos aplicativos. */
export const CAPTURE_SOURCES_TEXT = CAPTURE_SOURCES
  .map((source) => `${source.bank} (${source.packages.join(', ')})`)
  .join('; ');

/** Contato para pedir a exclusão dos dados. É o único lugar onde esse contato aparece. */
export const PRIVACY_CONTACT = 'salomaolucas13@outlook.com';

export const CAPTURE_CONSENT_TITLE = 'Captura de notificações bancárias';

export const CAPTURE_CONSENT_SECTIONS: readonly TextSection[] = [
  {
    title: 'O que o app lê',
    paragraphs: [
      `Somente as notificações dos aplicativos dos bancos ${listInPortuguese(CAPTURE_APPS)}, incluindo os outros aplicativos do ${listInPortuguese(BANKS_WITH_SEVERAL_APPS)} instalados no celular (por exemplo, o de empresas). Notificações de qualquer outro aplicativo são ignoradas.`,
      `Lista completa, pelo identificador de cada aplicativo no Android: ${CAPTURE_SOURCES_TEXT}.`,
      'Para isso o Android pede a permissão "Acesso às notificações".',
    ],
  },
  {
    title: 'O que é enviado',
    paragraphs: [
      'Apenas, quando a notificação é uma compra: o banco, o valor, o estabelecimento e a data e hora.',
      'O texto da notificação NÃO é enviado: ele é lido no seu celular, de onde só saem esses quatro dados.',
    ],
  },
  {
    title: 'Para quê',
    paragraphs: [
      'Para lançar a despesa automaticamente nas transações do seu grupo, sem você digitar. Os membros do grupo veem esses lançamentos.',
    ],
  },
  {
    title: 'Você decide',
    paragraphs: [
      'Dá para desligar quando quiser em Configurações. Desligar interrompe o envio na hora. A data do seu aceite fica registrada nas Configurações. Nada é enviado nem guardado sem o seu aceite.',
    ],
  },
];

export const AI_CHAT_TITLE = 'Chat IA e seus dados';

export const AI_CHAT_SECTIONS: readonly TextSection[] = [
  {
    title: 'Para responder, o Chat IA usa o Google Gemini',
    paragraphs: [
      'Ao enviar uma pergunta, o app manda ao Google Gemini a sua mensagem e, como contexto, a renda, o orçamento, os gastos e as metas do grupo.',
      'Sem isso a IA não consegue responder sobre as suas finanças.',
      'Se você aceitar, as descrições das linhas dos extratos que você importar também podem ser enviadas ao Google Gemini para sugerir a categoria de cada uma. Isso só acontece quando o servidor está com a IA ligada.',
    ],
  },
  {
    title: 'Se você não aceitar',
    paragraphs: ['O Chat IA fica desativado, nenhuma descrição de extrato vai ao Gemini e nada é enviado. O restante do app funciona normalmente.'],
  },
];

export const PRIVACY_TITLE = 'Privacidade';

const GEMINI_SHARING_WHEN_AVAILABLE =
  'Com o Google Gemini, somente depois que você aceita o aviso do Chat IA e somente quando o servidor está com a IA ligada: a sua pergunta e os dados financeiros do grupo (chat) e as descrições das linhas dos extratos importados (categorização).';

const GEMINI_SHARING_WHEN_UNAVAILABLE =
  'Com o Google Gemini, apenas quando o recurso de IA estiver disponível no app. Nesta versão ele está desligado e nada é enviado ao Gemini. Quando estiver disponível, o app vai pedir o seu aceite antes; só então poderão ser enviados a sua pergunta e os dados financeiros do grupo (chat) e as descrições das linhas dos extratos importados (categorização).';

/**
 * As seções da tela Privacidade. `aiAvailable` diz se o Chat IA existe nesta versão do app: quando não existe,
 * o texto não apresenta a IA como algo que o usuário possa usar ou tenha recusado.
 */
export function privacySections(aiAvailable: boolean): readonly TextSection[] {
  return [
    {
      title: 'Dados que o CoupleSync coleta',
      paragraphs: [
        'Conta: nome, e-mail e senha (a senha é guardada de forma irreversível, nunca em texto aberto).',
        'Finanças que você informa: transações, rendas, orçamento, metas e categorias.',
        'Importação de extratos: o PDF que você envia é lido no servidor para extrair as transações e é apagado depois de processado.',
        'Captura de notificações (só se você aceitar): banco, valor, estabelecimento e data e hora das compras. O texto das notificações não é enviado.',
        'Alertas: o código do seu aparelho para enviar notificações do app (push).',
      ],
    },
    {
      title: 'Onde ficam',
      paragraphs: [
        'No servidor do CoupleSync, em banco de dados com acesso restrito.',
        'No seu celular ficam a sessão (em armazenamento seguro do Android) e as suas respostas de consentimento. Ao sair da conta, a sessão e os dados do app somem do aparelho; só as respostas de consentimento ficam guardadas para esta conta.',
      ],
    },
    {
      title: 'Com quem são compartilhados',
      paragraphs: [
        'Com os membros do seu grupo: eles veem as transações, rendas, orçamento e metas do grupo.',
        aiAvailable ? GEMINI_SHARING_WHEN_AVAILABLE : GEMINI_SHARING_WHEN_UNAVAILABLE,
        'Com o Google Firebase Cloud Messaging, que entrega as notificações push do app ao seu aparelho (recebe o código do aparelho e o texto do alerta).',
        'Com os provedores de hospedagem e de banco de dados em que o servidor roda, que guardam os dados em nome do CoupleSync.',
        'Dependendo da configuração do servidor, o PDF do extrato pode ser lido pelo Azure Document Intelligence (Microsoft); por padrão a leitura é feita no próprio servidor.',
        'O app não tem publicidade e não envia dados a anunciantes.',
      ],
    },
    {
      title: 'Como pedir a exclusão',
      paragraphs: [
        `Para pedir a exclusão da sua conta e dos seus dados, escreva para ${PRIVACY_CONTACT}, informando o e-mail da conta. O app ainda não tem um botão para excluir a conta. Você pode apagar transações, metas e rendas direto no app e sair do grupo a qualquer momento.`,
      ],
    },
  ];
}

/** As linhas de "Suas respostas neste aparelho". Sem o recurso de IA no app, não há resposta de IA a mostrar. */
export function consentStatusLines(
  record: ConsentRecord,
  aiAvailable: boolean,
  formatDate: (iso: string) => string = formatConsentDate,
): string[] {
  const lines = [
    record.capture.acceptedAt
      ? `Captura de notificações: aceita em ${formatDate(record.capture.acceptedAt)} (${record.capture.enabled ? 'ligada' : 'desligada'}).`
      : 'Captura de notificações: não aceita.',
  ];
  if (aiAvailable) {
    lines.push(
      record.aiChat.acceptedAt
        ? `Chat IA (Google Gemini): aceito em ${formatDate(record.aiChat.acceptedAt)}.`
        : 'Chat IA (Google Gemini): não aceito.',
    );
  }
  return lines;
}
