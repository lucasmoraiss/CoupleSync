// Textos de privacidade exibidos dentro do app (sem link externo). Um só lugar para que a tela de consentimento,
// a tela "Privacidade" e o aviso do chat não se contradigam.

export interface TextSection {
  readonly title: string;
  readonly paragraphs: readonly string[];
}

/** Aplicativos cujas notificações a captura lê. Mantenha alinhado a notification-patterns.json e ao serviço nativo. */
export const CAPTURE_APPS: readonly string[] = ['Nubank', 'Itaú', 'Inter', 'C6 Bank', 'Bradesco'];

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
      'Dá para desligar quando quiser em Configurações. Desligar interrompe o envio na hora. A data do seu aceite fica registrada nas Configurações.',
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
    ],
  },
  {
    title: 'Se você não aceitar',
    paragraphs: ['O Chat IA fica desativado e nada é enviado. O restante do app funciona normalmente.'],
  },
];

export const PRIVACY_TITLE = 'Privacidade';

export const PRIVACY_SECTIONS: readonly TextSection[] = [
  {
    title: 'Dados que o CoupleSync coleta',
    paragraphs: [
      'Conta: nome, e-mail e senha (guardada de forma irreversível, nunca em texto aberto).',
      'Finanças que você informa: transações, rendas, orçamento, metas e categorias.',
      'Importação de extratos: as imagens ou PDFs que você envia são lidos para extrair as transações.',
      'Captura de notificações (só se você aceitar): banco, valor, estabelecimento e data e hora das compras. O texto das notificações não é enviado.',
      'Alertas: o código do seu aparelho para enviar notificações do app (push).',
    ],
  },
  {
    title: 'Onde ficam',
    paragraphs: [
      'No servidor do CoupleSync, em banco de dados protegido por acesso restrito.',
      'No seu celular ficam a sessão (em armazenamento seguro do Android) e as suas respostas de consentimento. Ao sair da conta, os dados da conta somem do aparelho.',
    ],
  },
  {
    title: 'Com quem são compartilhados',
    paragraphs: [
      'Com os membros do seu grupo: eles veem as transações, rendas, orçamento e metas do grupo.',
      'Com o Google Gemini, somente quando você aceita usar o Chat IA: a sua pergunta e os dados financeiros do grupo, para gerar a resposta.',
      'Não vendemos nem compartilhamos seus dados com anunciantes.',
    ],
  },
  {
    title: 'Como pedir a exclusão',
    paragraphs: [
      'Peça a exclusão da sua conta e dos seus dados a quem te convidou para o teste do CoupleSync (a pessoa responsável pelo app), informando o e-mail da conta. Você pode apagar transações, metas e rendas direto no app e sair do grupo a qualquer momento.',
    ],
  },
];
