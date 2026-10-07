// Para onde volta cada tela que é aba oculta. As abas não formam pilha: sem isto, "Voltar" dependeria do
// histórico de abas (e, com o padrão da biblioteca, cairia sempre no Painel).

/** Nome da tela (como registrada em app/(main)/_layout.tsx) → rota da tela de onde ela é aberta. */
export const PARENT_ROUTE = {
  'settings/alerts': '/(main)/settings',
  'settings/group': '/(main)/settings',
  'settings/change-password': '/(main)/settings',
  'settings/verify-email': '/(main)/settings',
  'settings/privacy': '/(main)/settings',
  'settings/capture-consent': '/(main)/settings',
  'transactions/new': '/(main)/transactions',
  'transactions/edit': '/(main)/transactions',
  'ocr-upload': '/(main)/transactions',
  'ocr-review': '/(main)/transactions',
} as const;

export type ChildScreen = keyof typeof PARENT_ROUTE;

export function parentRouteOf(screen: ChildScreen): string {
  return PARENT_ROUTE[screen];
}

/** `backBehavior` das abas: o botão voltar do Android refaz o caminho das abas visitadas, não pula para o Painel. */
export const TABS_BACK_BEHAVIOR = 'history' as const;
