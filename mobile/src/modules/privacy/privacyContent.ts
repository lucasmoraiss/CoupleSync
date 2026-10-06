// Textos de privacidade exibidos dentro do app (sem link externo). Um só lugar para que a tela de consentimento,
// a tela "Privacidade" e o aviso do chat não se contradigam.

export interface TextSection {
  readonly title: string;
  readonly paragraphs: readonly string[];
}

/** Aplicativos cujas notificações a captura lê. Mantenha alinhado a notification-patterns.json e ao serviço nativo. */
export const CAPTURE_APPS: readonly string[] = ['Nubank', 'Itaú', 'Inter', 'C6 Bank', 'Bradesco'];

/**
 * !!! TEMPORÁRIO !!! Contato para pedir a exclusão dos dados. Hoje é só uma descrição neutra (nenhum e-mail foi
 * definido). O responsável pelo app deve trocar por um contato real (ex.: um e-mail) ANTES de abrir o teste a mais
 * pessoas. É o único lugar onde esse contato aparece.
 */
export const PRIVACY_CONTACT = 'a pessoa responsável pelo app que te convidou para o teste do CoupleSync';

export const CAPTURE_CONSENT_TITLE = 'Captura de notificações bancárias';

export const CAPTURE_CONSENT_SECTIONS: readonly TextSection[] = [
  {
    title: 'O que o app lê',
    paragraphs: [
      `Somente as notificações dos aplicativos dos bancos ${CAPTURE_APPS.join(', ')}. Notificações de qualquer outro aplicativo são ignoradas.`,
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

export const PRIVACY_SECTIONS: readonly TextSection[] = [
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
      'Com o Google Gemini, somente depois que você aceita o aviso do Chat IA e somente quando o servidor está com a IA ligada: a sua pergunta e os dados financeiros do grupo (chat) e as descrições das linhas dos extratos importados (categorização).',
      'Com o Google Firebase Cloud Messaging, que entrega as notificações push do app ao seu aparelho (recebe o código do aparelho e o texto do alerta).',
      'Com os provedores de hospedagem e de banco de dados em que o servidor roda, que guardam os dados em nome do CoupleSync.',
      'Dependendo da configuração do servidor, o PDF do extrato pode ser lido pelo Azure Document Intelligence (Microsoft); por padrão a leitura é feita no próprio servidor.',
      'O app não tem publicidade e não envia dados a anunciantes.',
    ],
  },
  {
    title: 'Como pedir a exclusão',
    paragraphs: [
      `Peça a exclusão da sua conta e dos seus dados a ${PRIVACY_CONTACT}, informando o e-mail da conta. O app ainda não tem um botão para excluir a conta. Você pode apagar transações, metas e rendas direto no app e sair do grupo a qualquer momento.`,
    ],
  },
];
