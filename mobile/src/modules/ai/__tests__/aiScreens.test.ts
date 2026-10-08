// Issue #38: o que as telas da análise com IA têm de ter. O Jest daqui não renderiza React Native, então estes
// testes leem o código das telas (como os de navegação): rótulos acessíveis que os fluxos Maestro usam, de onde
// vem cada decisão e o que NÃO pode mais existir (a aba, a variável de build, o aceite guardado no aparelho).
import * as fs from 'fs';
import * as path from 'path';
import { PARENT_ROUTE } from '@/navigation/routes';

const MOBILE_DIR = path.resolve(__dirname, '../../../..');
const REPO_DIR = path.resolve(MOBILE_DIR, '..');
const read = (relative: string) => fs.readFileSync(path.join(MOBILE_DIR, relative), 'utf8');

/** Todos os arquivos de código e configuração do repositório que são versionados por este trabalho. */
function sourceFiles(dir: string, found: string[] = []): string[] {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['node_modules', '.git', 'bin', 'obj', '.expo', 'android', 'ios', 'dist', 'app-e2e-out', 'TestResults'].includes(entry.name)) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) sourceFiles(full, found);
    else if (/\.(ts|tsx|js|json|cs|csproj|yml|yaml|sh|md|ps1)$/.test(entry.name)) found.push(full);
  }
  return found;
}

describe('o Assistente não é mais uma aba', () => {
  const layout = read('app/(main)/_layout.tsx');

  it('não existe aba "Chat IA"; a rota chat/index é tela oculta com o título "Assistente"', () => {
    expect(layout).not.toMatch(/Chat IA/);
    expect(layout).toContain(`<Tabs.Screen name="chat/index" options={{ title: 'Assistente', href: null }} />`);
  });

  it('as telas novas são abas ocultas e voltam para onde foram abertas', () => {
    expect(layout).toContain('<Tabs.Screen name="ai/welcome" options={{ href: null }} />');
    expect(layout).toContain('<Tabs.Screen name="settings/ai" options={{ href: null }} />');
    expect(PARENT_ROUTE['ai/welcome']).toBe('/');
    expect(PARENT_ROUTE['chat/index']).toBe('/');
    expect(PARENT_ROUTE['settings/ai']).toBe('/(main)/settings');
  });

  it('sobram as sete abas visíveis de antes, menos a do chat', () => {
    const visibleTabs = [...layout.matchAll(/<Tabs\.Screen name="([^"]+)" options=\{\{ title: '([^']+)', tabBarLabel:/g)].map((m) => m[2]);
    expect(visibleTabs).toEqual(['Painel', 'Transações', 'Metas', 'Fluxo', 'Rendas', 'Relatórios', 'Config']);
  });
});

describe('nada mais decide a IA na geração do pacote nem pelo aceite guardado no aparelho', () => {
  it('o módulo aiAvailability não existe mais', () => {
    expect(fs.existsSync(path.join(MOBILE_DIR, 'src/modules/chat/aiAvailability.ts'))).toBe(false);
  });

  it('nenhuma referência às chaves antigas sobra no código, nos scripts nem na configuração', () => {
    // Montadas por partes para este arquivo não ser, ele mesmo, uma referência.
    const oldNames = [['AI', 'CHAT', 'ENABLED'].join('_'), ['AI', 'FEATURE', 'ENABLED'].join('_')];
    const self = path.resolve(__filename);
    const roots = ['backend/src', 'backend/tests', 'mobile/app', 'mobile/src', 'mobile/plugins', 'mobile/tests', 'scripts', '.github/workflows']
      .map((relative) => path.join(REPO_DIR, relative))
      .filter((dir) => fs.existsSync(dir));
    const files = [
      ...roots.flatMap((dir) => sourceFiles(dir)),
      ...['render.yaml', 'backend/README.md', 'mobile/README.md', 'mobile/app.json', 'mobile/eas.json'].map((relative) => path.join(REPO_DIR, relative)),
    ].filter((file) => path.resolve(file) !== self && fs.existsSync(file));
    expect(files.length).toBeGreaterThan(200);

    const offenders = files.filter((file) => {
      const text = fs.readFileSync(file, 'utf8');
      return oldNames.some((name) => text.includes(name));
    });
    expect(offenders.map((file) => path.relative(REPO_DIR, file))).toEqual([]);
  });

  it('o Assistente e o envio de extrato não consultam o aceite local (aiChat): quem decide é GET /ai/status', () => {
    for (const file of [
      'src/modules/chat/api/chatApi.ts',
      'src/modules/chat/hooks/useChat.ts',
      'src/modules/chat/screens/ChatScreen.tsx',
      'app/(main)/chat/index.tsx',
      'app/(main)/ocr-upload.tsx',
    ]) {
      const source = read(file);
      expect(source).not.toMatch(/consentStore|isAiChatAllowed|acceptAiChat|declineAiChat|record\.aiChat/);
    }
    const screen = read('src/modules/chat/screens/ChatScreen.tsx');
    expect(screen).toMatch(/assistantGate\(status, loadFailed\)/);
    expect(screen).toMatch(/useAiStatus\(\)/);
    expect(read('app/(main)/ocr-upload.tsx')).toMatch(/appendAiConsent\(formData, aiUploadConsent\(currentAiStatus\(\)\)\)/);
  });

  it('o registro local de consentimento continua com o campo aiChat (não é apagado nem migrado)', () => {
    const consent = read('src/modules/privacy/consent.ts');
    expect(consent).toMatch(/export const CONSENT_VERSION = 1;/);
    expect(consent).toMatch(/aiChat: \{\s*acceptedAt: isoOrNull\(data\.aiChat\?\.acceptedAt\)/);
  });
});

describe('tela do Assistente', () => {
  const screen = read('src/modules/chat/screens/ChatScreen.tsx');
  const hook = read('src/modules/chat/hooks/useChat.ts');

  it('chama-se Assistente, tem como voltar ao Painel e rótulos acessíveis na pergunta e no envio', () => {
    expect(screen).toMatch(/accessibilityRole="header">Assistente</);
    expect(screen).toMatch(/accessibilityLabel="Voltar para o Painel"/);
    expect(screen).toMatch(/goToParent\('chat\/index'\)/);
    expect(screen).toMatch(/accessibilityLabel="Pergunta para o Assistente"/);
    expect(screen).toMatch(/accessibilityLabel="Enviar mensagem"/);
    expect(screen).not.toMatch(/Chat IA/);
  });

  it('sem a IA ativada mostra a explicação e o botão Ativar, que leva à tela de boas-vindas', () => {
    expect(screen).toMatch(/gate === 'needs-activation'/);
    expect(screen).toMatch(/actionLabel="Ativar"/);
    expect(screen).toMatch(/router\.push\('\/\(main\)\/ai\/welcome'/);
  });

  it('tem estado de espera que muda quando a resposta demora, e sugestões de pergunta na tela vazia', () => {
    expect(screen).toMatch(/Pensando na resposta…/);
    expect(screen).toMatch(/Ainda pensando… a resposta pode levar até meio minuto\./);
    expect(screen).toMatch(/accessibilityLiveRegion="polite"/);
    expect(screen).toMatch(/SUGGESTED_QUESTIONS\.map/);
    expect(screen).toMatch(/accessibilityLabel=\{`Perguntar: \$\{question\}`\}/);
  });

  it('carregando, erro (com tentar de novo) e indisponível têm tela própria', () => {
    expect(screen).toMatch(/gate === 'loading'/);
    expect(screen).toMatch(/gate === 'error'/);
    expect(screen).toMatch(/<ErrorState message="Não foi possível abrir o Assistente/);
    expect(screen).toMatch(/gate === 'unavailable'/);
  });

  it('o erro de envio usa as frases por desfecho e consulta o status de novo quando a IA foi desligada', () => {
    expect(hook).toMatch(/setError\(chatErrorMessage\(err\)\)/);
    expect(hook).toMatch(/if \(chatErrorChangesStatus\(err\)\) void useAiStatusStore\.getState\(\)\.refresh\(\)/);
  });
});

describe('tela de boas-vindas da análise com IA', () => {
  const screen = read('app/(main)/ai/welcome.tsx');

  it('tem a pergunta, três linhas do que a IA faz, a linha do que vai e para onde e "Ler tudo" com os sete pontos', () => {
    expect(screen).toMatch(/Quer que a IA analise as finanças do grupo\?/);
    expect((/const WHAT_IT_DOES: readonly string\[\] = \[([\s\S]*?)\];/.exec(screen)?.[1].match(/^\s+'/gm) ?? []).length).toBe(3);
    expect(screen).toMatch(/\{AI_ANALYSIS_SUMMARY\}/);
    expect(screen).toMatch(/'Ler tudo sobre a análise com IA'/);
    expect(screen).toMatch(/AI_ANALYSIS_POINTS\.map/);
  });

  it('tem os dois botões da pergunta, com rótulos acessíveis', () => {
    expect(screen).toMatch(/accessibilityLabel="Ativar a análise com IA para o grupo"/);
    expect(screen).toMatch(/>Ativar para o grupo</);
    expect(screen).toMatch(/accessibilityLabel="Agora não ativar a análise com IA"/);
    expect(screen).toMatch(/>Agora não</);
  });

  it('quando outra pessoa já ativou, avisa quem foi e oferece Entendi e Desligar para o grupo', () => {
    expect(screen).toMatch(/\$\{view\.names\} ativou a análise com IA para o grupo/);
    expect(screen).toMatch(/accessibilityLabel="Entendi, manter a análise com IA ativada"/);
    expect(screen).toMatch(/accessibilityLabel="Desligar a análise com IA para o grupo"/);
    expect(screen).toMatch(/store\.revoke\('group'\)/);
  });

  it('"Agora não" e "Entendi" gravam a resposta no servidor; ativar envia o aceite', () => {
    expect(screen).toMatch(/const dismiss = \(\) => run\('dismiss', store\.answerOnboarding\)/);
    expect(screen).toMatch(/const activate = \(\) => run\('activate', store\.activate,/);
  });

  it('IA indisponível, pergunta já respondida ou status que não carregou: volta ao Painel; falha ao gravar fica na tela e avisa', () => {
    expect(screen).toMatch(/view\.kind === 'unavailable' \|\| view\.kind === 'done'/);
    expect(screen).toMatch(/status === null && loadFailed/);
    expect(screen).toMatch(/goToParent\('ai\/welcome'\)/);
    expect(screen).toMatch(/accessibilityRole="alert"/);
  });

  it('é remontada a cada visita e não usa router.back()', () => {
    expect(screen).toMatch(/export default resetOnFocus\(AiWelcomeScreen\)/);
    expect(screen).not.toMatch(/router\.back\(\)/);
  });
});

describe('Painel', () => {
  const dashboard = read('app/(main)/index.tsx');

  it('consulta o status da IA a cada foco e tem o botão fixo "Assistente" no cabeçalho, só quando há IA no servidor', () => {
    expect(dashboard).toMatch(/const ai = useAiStatus\(\(fresh\) => \{/);
    expect(dashboard).toMatch(/const aiStatus = ai\.status;/);
    expect(dashboard).toMatch(/\{isAssistantVisible\(aiStatus\) && \(/);
    expect(dashboard).toMatch(/accessibilityLabel="Abrir o Assistente"/);
    expect(dashboard).toMatch(/router\.push\('\/\(main\)\/chat'/);
  });

  it('mostra o cartão "Análise com IA desligada — Ativar" enquanto o grupo não ativou', () => {
    expect(dashboard).toMatch(/\{shouldShowActivationCard\(aiStatus\) && \(/);
    expect(dashboard).toMatch(/accessibilityLabel="Análise com IA desligada\. Ativar"/);
    expect(dashboard).toMatch(/>Análise com IA desligada</);
  });

  it('leva à tela de boas-vindas quando a pergunta é devida, uma vez por abertura do app', () => {
    // A regra (devida, uma vez, Painel em foco, a captura primeiro) está em openWelcomeIfDue, testada em aiStatusStore.test.ts.
    expect(dashboard).toMatch(
      /void openWelcomeIfDue\(fresh, \{\s*isFocused: ai\.isFocused,\s*otherPromptPending: isCaptureConsentAhead,\s*open: \(\) => router\.push\('\/\(main\)\/ai\/welcome' as any\),\s*\}\);/,
    );
    const store = read('src/modules/ai/aiStatusStore.ts');
    expect(store).toMatch(/if \(!shouldOpenWelcome\(fresh, wasWelcomeShown\(\)\)\) return false;/);
    expect(store).toMatch(/useAiStatusStore\.getState\(\)\.markWelcomeShown\(\);\s*deps\.open\(\);/);
  });

  it('sem status nenhum, diz o que está acontecendo com a IA e oferece verificar de novo (revisão 1, I3)', () => {
    expect(dashboard).toMatch(/const aiNotice = aiStatusNotice\(aiStatus, ai\.loadFailed, ai\.retrying\);/);
    expect(dashboard).toMatch(/\{aiNotice !== 'none' && \(/);
    expect(dashboard).toMatch(/\{AI_STATUS_NOTICE_TEXT\[aiNotice\]\}/);
    expect(dashboard).toMatch(/onPress=\{\(\) => void ai\.refresh\(\)\}\s*accessibilityLabel="Verificar a análise com IA agora"/);
    // Os fluxos Maestro afirmam que "Tentar novamente" (botão das telas de erro) não aparece: o aviso da IA não usa esse texto.
    expect(dashboard).not.toMatch(/Tentar novamente/);
  });

  it('puxar para atualizar consulta também o status da IA', () => {
    expect(dashboard).toMatch(/await Promise\.all\(\[refetch\(\), ai\.refresh\(\)\]\);/);
  });

  it('fala em grupo, não em casal', () => {
    expect(dashboard).not.toMatch(/casal/i);
  });
});

describe('o status chega às telas a cada resposta do servidor, não só na consulta do foco', () => {
  const hook = read('src/modules/ai/useAiStatus.ts');

  it('a tela em foco é avisada quando a contagem de respostas do servidor sobe (ex.: nova tentativa que deu certo)', () => {
    expect(hook).toMatch(/const freshCount = useAiStatusStore\(\(state\) => state\.freshCount\);/);
    expect(hook).toMatch(/if \(freshCount === 0 \|\| !focused\.current\) return;/);
    expect(hook).toMatch(/\}, \[freshCount\]\);/);
    expect(hook).toMatch(/focused\.current = true;\s*void refresh\(\);/);
  });
});

describe('teclado no Assistente', () => {
  it('no Android a tela não desconta o teclado por conta própria (a janela já encolhe): o botão de enviar fica à vista com o teclado aberto', () => {
    const screen = read('src/modules/chat/screens/ChatScreen.tsx');
    expect(screen).toMatch(/behavior=\{Platform\.OS === 'ios' \? 'padding' : undefined\}/);
    expect(screen).not.toMatch(/'height'/);
    expect(screen).not.toMatch(/keyboardVerticalOffset/);
  });
});

describe('a pergunta da IA e o consentimento da captura não disputam a tela', () => {
  it('a captura marca que está abrindo antes de gravar e de navegar, e desfaz a marca se não abriu', () => {
    const hook = read('src/modules/integrations/notification-capture/useCaptureConsentSync.ts');
    expect(hook).toMatch(
      /noteCapturePromptOpening\(sessionUserId\);\s*void useConsentStore\.getState\(\)\.markCapturePromptShown\(\)\.then\(\(recorded\) => \{\s*if \(recorded\) router\.push\(CAPTURE_CONSENT_ROUTE as any\);\s*else clearCapturePromptOpening\(\);/,
    );
    expect(hook).toMatch(/export function isCaptureConsentAhead\(\): Promise<boolean> \{\s*return isCapturePromptAhead\(\{/);
  });
});

describe('criar ou entrar num grupo', () => {
  const setup = read('app/(auth)/couple-setup.tsx');

  it('depois de criar ("Ir para o Painel") e depois de entrar, o destino vem do status da IA', () => {
    expect(setup).toMatch(/router\.replace\(\(await routeAfterGroupSetup\(undefined, isCaptureConsentAhead\)\) as any\)/);
    expect(setup).toMatch(/announceGroupChange\(\);\s*await enterApp\(\);/);
    expect(setup).toMatch(/const goToHome = async \(\) => \{[\s\S]*?await enterApp\(\);/);
  });
});

describe('Configurações', () => {
  const settings = read('app/(main)/settings/index.tsx');
  const ai = read('app/(main)/settings/ai.tsx');

  it('tem a entrada "Inteligência artificial"', () => {
    expect(settings).toContain('Inteligência artificial');
    expect(settings).toMatch(/router\.push\('\/\(main\)\/settings\/ai'/);
    expect(settings).toMatch(/accessibilityLabel="Abrir a inteligência artificial: ativar, desligar e ver o consumo"/);
  });

  it('a tela mostra o estado, ativar, desligar para o grupo e retirar o próprio aceite', () => {
    expect(ai).toMatch(/activationSummary\(status, formatConsentDate\)/);
    expect(ai).toMatch(/accessibilityLabel="Ativar a análise com IA: ver o que é enviado e decidir"/);
    expect(ai).toMatch(/accessibilityLabel="Desligar a análise com IA para o grupo"/);
    expect(ai).toMatch(/revoke\('group'\)/);
    expect(ai).toMatch(/accessibilityLabel="Retirar o meu aceite da análise com IA"/);
    expect(ai).toMatch(/revoke\('mine'\)/);
  });

  it('mostra o consumo: orçamento do grupo, hoje, 30 dias e a cota de cada modelo', () => {
    expect(ai).toMatch(/aiApiClient\.getUsage\(USAGE_DAYS\)/);
    expect(ai).toMatch(/const USAGE_DAYS = 30;/);
    expect(ai).toMatch(/groupBudgetText\(data\.groupBudget\)/);
    expect(ai).toMatch(/usageLine\('Hoje', usageTotals\(data\.days\)\.today\)/);
    expect(ai).toMatch(/usageTotals\(data\.days\)\.period/);
    expect(ai).toMatch(/modelQuotaText\(provider\)/);
  });

  it('tem carregando, erro com tentar de novo, indisponível e o caminho para a privacidade; não desenha o que é de outras fases', () => {
    expect(ai).toMatch(/<LoadingState \/>/);
    expect(ai).toMatch(/<ErrorState message="Não foi possível carregar/);
    expect(ai).toMatch(/A análise com IA não está disponível no momento/);
    expect(ai).toMatch(/router\.push\('\/\(main\)\/settings\/privacy'/);
    expect(ai).not.toMatch(/Apagar histórico|e-mail semanal|weeklyEmail/i);
    expect(ai).toMatch(/goToParent\('settings\/ai'\)/);
  });
});

describe('fluxos Maestro', () => {
  const flow = read('tests/e2e/flows/08-ia-ativar-e-assistente.yaml');
  const shared = read('tests/e2e/flows/comum/conta-com-grupo-no-painel.yaml');
  const runner = fs.readFileSync(path.join(REPO_DIR, 'scripts/app-e2e-run-flows.sh'), 'utf8');
  const api = fs.readFileSync(path.join(REPO_DIR, 'scripts/app-e2e-api.sh'), 'utf8');

  it('o fluxo novo está na lista dos que rodam, e a API de teste usa o provedor falso', () => {
    expect(runner).toContain('"08-ia-ativar-e-assistente"');
    expect(api).toContain('Ai__UseFakeProvider=true');
  });

  it('todo rótulo que o fluxo novo toca existe numa tela, igual', () => {
    const screens = [
      read('app/(main)/ai/welcome.tsx'),
      read('app/(main)/index.tsx'),
      read('src/modules/chat/screens/ChatScreen.tsx'),
    ].join('\n');
    for (const label of [
      'Ativar a análise com IA para o grupo',
      'Abrir o Assistente',
      'Pergunta para o Assistente',
      'Enviar mensagem',
      'Voltar para o Painel',
    ]) {
      expect(flow).toContain(`"${label}"`);
      expect(screens).toContain(`accessibilityLabel="${label}"`);
    }
    expect(flow).toContain('Quer que a IA analise as finanças do grupo\\\\?');
    expect(flow).toContain('Análise com IA desligada');
    // A resposta fixa do provedor falso da API (FakeLlmProvider).
    expect(flow).toContain('Esta é uma resposta de teste do assistente, sem dados reais.');
    const fake = fs.readFileSync(path.join(REPO_DIR, 'backend/src/CoupleSync.Infrastructure/Integrations/Llm/LlmProviders.cs'), 'utf8');
    expect(fake).toContain('Esta é uma resposta de teste do assistente, sem dados reais.');
  });

  it('os fluxos que já existiam respondem "Agora não" à tela de boas-vindas antes de chegar ao Painel', () => {
    expect(read('tests/e2e/flows/comum/responder-agora-nao-a-ia.yaml')).toContain('"Agora não ativar a análise com IA"');
    expect(shared).toContain('- runFlow: responder-agora-nao-a-ia.yaml');
    expect(read('tests/e2e/flows/01-cadastro-e-grupo.yaml')).toContain('comum/responder-agora-nao-a-ia.yaml');
    expect(read('app/(main)/ai/welcome.tsx')).toContain('accessibilityLabel="Agora não ativar a análise com IA"');
  });

  it('o fluxo 03 (duas contas no mesmo grupo) passa por "X ativou", Entendi, e por Configurações > IA; todo rótulo que ele toca existe numa tela', () => {
    const flow03 = read('tests/e2e/flows/03-entrar-em-grupo.yaml');
    const screens = [
      read('app/(main)/ai/welcome.tsx'),
      read('app/(main)/index.tsx'),
      read('app/(main)/settings/index.tsx'),
      read('app/(main)/settings/ai.tsx'),
    ].join('\n');
    for (const label of [
      'Ativar a análise com IA para o grupo',
      'Entendi, manter a análise com IA ativada',
      'Desligar a análise com IA para o grupo',
      'Abrir o Assistente',
      'Abrir a inteligência artificial: ativar, desligar e ver o consumo',
      'Ativar a análise com IA: ver o que é enviado e decidir',
      'Retirar o meu aceite da análise com IA',
      'Voltar para as configurações',
      'Análise com IA desligada. Ativar',
    ]) {
      expect(flow03).toContain(`"${label}"`);
      expect(screens).toContain(`accessibilityLabel="${label}"`);
    }
    // O aviso leva o primeiro nome de quem ativou; Configurações mostra o nome inteiro e a data.
    expect(flow03).toContain('"Caio ativou a análise com IA para o grupo"');
    expect(read('app/(main)/ai/welcome.tsx')).toContain('`${view.names} ativou a análise com IA para o grupo`');
    expect(flow03).toContain('"Ativada por Caio Teste em .*"');
    expect(flow03).toContain('"Ativada por Davi Teste em .*"');
    // A confirmação de desligar: título e botão do aviso da tela.
    expect(flow03).toContain('"Desligar para o grupo\\\\?"');
    expect(read('app/(main)/settings/ai.tsx')).toContain("'Desligar para o grupo?'");
    expect(read('app/(main)/settings/ai.tsx')).toContain("text: 'Desligar',");
    expect(flow03.match(/visible: "Desligada"/g)).toHaveLength(2);
  });

  it('os fluxos 01 e 06 rolam o Painel até o atalho das transações (o cartão da IA pode empurrá-lo para baixo da dobra)', () => {
    for (const name of ['01-cadastro-e-grupo.yaml', '06-abas.yaml']) {
      const flow = read(`tests/e2e/flows/${name}`);
      expect(flow).toMatch(/- scrollUntilVisible:\s*element: "Ver todas as transações"\s*direction: DOWN/);
      expect(flow).not.toContain('- assertVisible: "Ver todas as transações"');
    }
  });

  it('nenhum texto novo do Painel ou das telas de IA casa com o que os fluxos afirmam que não aparece', () => {
    for (const file of ['app/(main)/index.tsx', 'app/(main)/ai/welcome.tsx']) {
      expect(read(file)).not.toMatch(/Tentar novamente|Algo deu errado/);
    }
  });
});
