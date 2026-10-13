// O que as telas da análise com IA têm de ter. O Jest daqui não renderiza React Native, então estes testes leem
// o código das telas (como os de navegação): rótulos acessíveis que os fluxos Maestro usam, de onde vem cada
// decisão e o que NÃO pode mais existir (a aba, a variável de build, o aceite guardado no aparelho — e, com a IA
// ligada para todo grupo, a pergunta de ativação: a tela de boas-vindas, o cartão "Ativar", "Retirar meu aceite").
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

  it('as telas de IA são abas ocultas e voltam para onde foram abertas', () => {
    expect(layout).toContain('<Tabs.Screen name="settings/ai" options={{ href: null }} />');
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

  // Issue #60, item 13: o interruptor de emergência é posto no painel do Render. Um valor versionado no arquivo
  // ("false") poderia desfazer o "true" de uma emergência numa sincronização; o arquivo só explica.
  it('render.yaml não declara o interruptor de emergência da IA; só explica onde ele é ligado', () => {
    const blueprint = fs.readFileSync(path.join(REPO_DIR, 'render.yaml'), 'utf8');
    const declared = blueprint.split(/\r?\n/).filter((line) => !line.trim().startsWith('#') && line.includes('Ai__Disabled'));
    expect(declared).toEqual([]);
    expect(blueprint).toMatch(/# Interruptor de emergência: a variável Ai__Disabled=true, posta no PAINEL do Render/);
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

  it('abre direto na conversa: a única coisa entre a pessoa e ela é o que o servidor diz (carregando, erro, indisponível, desligada para o grupo)', () => {
    const gates = [...screen.matchAll(/if \(gate === '([a-z-]+)'\)/g)].map((m) => m[1]);
    expect(gates).toEqual(['loading', 'error', 'unavailable', 'off-for-group']);
    expect(screen).toMatch(/\}\s*return <ChatConversation \/>;/);
    expect(screen).not.toMatch(/needs-activation/);
  });

  it('com a IA desligada para o grupo diz isso e leva a Configurações > Inteligência artificial, sem falar em ativar nem em aceite', () => {
    const notice = /if \(gate === 'off-for-group'\) \{([\s\S]*?)\n  \}/.exec(screen)?.[1] ?? '';
    expect(notice).toMatch(/title="A IA está desligada para este grupo"/);
    expect(notice).toMatch(/actionAccessibilityLabel="Abrir as configurações de inteligência artificial para ligar de novo"/);
    expect(notice).toMatch(/router\.push\('\/\(main\)\/settings\/ai' as any\)/);
    expect(notice).not.toMatch(/ativ|aceit/i);
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

describe('não existe mais pergunta de ativação: nenhuma tela de boas-vindas, e nenhum caminho do app leva a ela', () => {
  /** Todo o código do app (telas e módulos), menos os testes. */
  const appFiles = ['app', 'src']
    .flatMap((dir) => sourceFiles(path.join(MOBILE_DIR, dir)))
    .filter((file) => /\.(ts|tsx)$/.test(file) && !file.includes(`${path.sep}__tests__${path.sep}`));

  it('a tela e o gancho dela foram removidos, e a rota não está registrada nas abas nem na tabela de "voltar"', () => {
    expect(fs.existsSync(path.join(MOBILE_DIR, 'app/(main)/ai/welcome.tsx'))).toBe(false);
    expect(fs.existsSync(path.join(MOBILE_DIR, 'app/(main)/ai'))).toBe(false);
    expect(fs.existsSync(path.join(MOBILE_DIR, 'src/modules/ai/useLeaveWelcome.ts'))).toBe(false);
    expect(read('app/(main)/_layout.tsx')).not.toMatch(/ai\/welcome/);
    expect(Object.keys(PARENT_ROUTE)).not.toContain('ai/welcome');
    expect(Object.keys(PARENT_ROUTE).filter((screen) => screen.startsWith('ai/'))).toEqual([]);
  });

  it('nenhum arquivo do app navega para a rota, nem usa as regras que abriam a pergunta', () => {
    expect(appFiles.length).toBeGreaterThan(100);
    const offenders = appFiles.filter((file) =>
      /ai\/welcome|AI_WELCOME_ROUTE|routeAfterGroupSetup|openWelcomeIfDue|shouldOpenWelcome|markWelcomeShown|answerOnboarding|shouldShowActivationCard/.test(
        fs.readFileSync(file, 'utf8'),
      ),
    );
    expect(offenders.map((file) => path.relative(MOBILE_DIR, file))).toEqual([]);
  });

  it('nenhuma tela decide nada por `onboardingPending`: mesmo que uma API anterior o devolva verdadeiro, nada abre', () => {
    const readers = appFiles.filter((file) => /onboardingPending/.test(fs.readFileSync(file, 'utf8'))).map((file) => path.relative(MOBILE_DIR, file).replace(/\\/g, '/'));
    // O campo só é descrito (tipo da resposta) e conferido na forma do valor guardado no aparelho; ninguém age por ele.
    expect(readers.sort()).toEqual(['src/modules/ai/aiStatus.ts', 'src/types/api.ts']);
    const rules = read('src/modules/ai/aiStatus.ts');
    expect(rules.match(/onboardingPending/g)).toHaveLength(2);
    expect(rules).toMatch(/typeof status\.onboardingPending === 'boolean'/);
  });

  it('nenhum texto do app pede para ativar a análise com IA nem oferece retirar aceite', () => {
    const offenders = appFiles.filter((file) =>
      /Ativar para o grupo|Ativar a análise com IA|Análise com IA desligada\. Ativar|Retirar (o )?meu aceite|Agora não ativar|Quer que a IA analise/.test(
        fs.readFileSync(file, 'utf8'),
      ),
    );
    expect(offenders.map((file) => path.relative(MOBILE_DIR, file))).toEqual([]);
  });
});

describe('Painel', () => {
  const dashboard = read('app/(main)/index.tsx');

  it('consulta o status da IA a cada foco e tem o botão fixo "Assistente" no cabeçalho, só quando há IA no servidor', () => {
    expect(dashboard).toMatch(/const ai = useAiStatus\(\);/);
    expect(dashboard).toMatch(/const aiStatus = ai\.status;/);
    expect(dashboard).toMatch(/\{isAssistantVisible\(aiStatus\) && \(/);
    expect(dashboard).toMatch(/accessibilityLabel="Abrir o Assistente"/);
    expect(dashboard).toMatch(/router\.push\('\/\(main\)\/chat'/);
  });

  it('não tem cartão de ativar a análise com IA, em estado nenhum do status', () => {
    expect(dashboard).not.toMatch(/Análise com IA desligada/);
    expect(dashboard).not.toMatch(/>Ativar</);
  });

  it('não abre tela nenhuma por causa do status da IA: não reage às respostas do servidor nem navega para outra coisa que o Assistente', () => {
    // Sem função de "chegou resposta": o status só é lido para desenhar o botão e o aviso.
    expect(dashboard).not.toMatch(/useAiStatus\(\(/);
    const aiRoutes = [...dashboard.matchAll(/router\.push\('([^']+)'/g)].map((m) => m[1].replace('/(main)', '')).filter((route) => /^\/(ai|chat|settings\/ai)(\/|$)/.test(route));
    expect(aiRoutes).toEqual(['/chat']);
  });

  it('sem status nenhum, diz o que está acontecendo com a IA e oferece verificar de novo (revisão 1, I3)', () => {
    // O que o aviso diz, quando tem botão e o que o toque faz está em aiStatusNoticeView, exercitado com o store
    // de verdade em aiStatusStore.test.ts ("o aviso do Painel sobre a IA"); aqui, só que a tela desenha a partir dele.
    expect(dashboard).toMatch(/const aiNotice = aiStatusNoticeView\(aiStatus, ai\.loadFailed, ai\.retrying, ai\.refresh\);/);
    expect(dashboard).toMatch(/\{aiNotice !== null && \(/);
    expect(dashboard).toMatch(/<Ionicons name=\{aiNotice\.icon\}/);
    expect(dashboard).toMatch(/accessibilityLiveRegion="polite">\{aiNotice\.text\}</);
    expect(dashboard).toMatch(/\{aiNotice\.checkNow !== null && \(/);
    expect(dashboard).toMatch(/onPress=\{aiNotice\.checkNow\}\s*accessibilityLabel="Verificar a análise com IA agora"/);
    expect(dashboard).toMatch(/>Verificar agora</);
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

describe('o consentimento da captura continua marcando que está abrindo (a pergunta da IA, que esperava por ele, não existe mais)', () => {
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

  it('depois de criar ("Ir para o Painel") e depois de entrar, o destino é o Painel, direto: não depende do status da IA', () => {
    expect(setup).toMatch(/const enterApp = async \(\) => \{\s*router\.replace\('\/' as any\);\s*\};/);
    expect(setup).toMatch(/announceGroupChange\(\);\s*await enterApp\(\);/);
    expect(setup).toMatch(/const goToHome = async \(\) => \{[\s\S]*?await enterApp\(\);/);
    expect(setup).not.toMatch(/aiStatus|AiStatus/);
  });
});

describe('Configurações', () => {
  const settings = read('app/(main)/settings/index.tsx');
  const ai = read('app/(main)/settings/ai.tsx');

  it('tem a entrada "Inteligência artificial"', () => {
    expect(settings).toContain('Inteligência artificial');
    expect(settings).toMatch(/router\.push\('\/\(main\)\/settings\/ai'/);
    expect(settings).toMatch(/accessibilityLabel="Abrir a inteligência artificial: ligar ou desligar e ver o consumo"/);
  });

  it('a tela mostra o estado (ligada, desligada para o grupo ou indisponível) e o desligar para o grupo, com confirmação', () => {
    expect(ai).toMatch(/const state = status \? aiGroupState\(status\) : null;/);
    expect(ai).toMatch(/<Text style=\{styles\.state\}>\{AI_GROUP_STATE_TEXT\[state\]\}<\/Text>/);
    for (const state of ['unavailable', 'on', 'off-for-group']) expect(ai).toContain(`{state === '${state}' && (`);
    expect(ai).toMatch(/\{actions\.turnOffForGroup && \(\s*<TouchableOpacity[\s\S]*?onPress=\{confirmGroupOff\}[\s\S]*?accessibilityLabel="Desligar a análise com IA para o grupo"/);
    expect(ai).toMatch(/onPress: \(\) => void change\(\(\) => useAiStatusStore\.getState\(\)\.turnOffForGroup\(\),/);
  });

  it('ligar de novo é direto: o botão chama o servidor, sem navegar para tela nenhuma', () => {
    expect(ai).toMatch(/\{actions\.turnOn && \(\s*<TouchableOpacity[\s\S]*?onPress=\{turnOn\}[\s\S]*?accessibilityLabel="Ligar de novo a análise com IA para o grupo"/);
    expect(ai).toMatch(/const turnOn = \(\) => \{\s*void change\(\(\) => useAiStatusStore\.getState\(\)\.turnOn\(\), 'Análise com IA ligada para o grupo\.'\);\s*\};/);
    // A única navegação da tela é para a Privacidade (e o voltar).
    expect([...ai.matchAll(/router\.push\('([^']+)'/g)].map((m) => m[1])).toEqual(['/(main)/settings/privacy']);
  });

  it('não há mais "Retirar meu aceite", "ativada por", nem texto que mande ativar ou aceitar', () => {
    expect(ai).not.toMatch(/withdrawMine|revoke\('mine'\)|Retirar/);
    expect(ai).not.toMatch(/activationSummary|formatConsentDate|acceptedBy|myAcceptance/);
    // Só os textos que a pessoa lê (entre aspas ou dentro das tags), sem os comentários do arquivo.
    const visible = ai.replace(/^\s*\/\/.*$/gm, '').replace(/\{\/\*[\s\S]*?\*\/\}/g, '');
    expect(visible).not.toMatch(/\bativ(ar|e|ada|ado|ação)\b|aceit/i);
  });

  it('mostra, como informação, o que vai e para onde, com os destinos que o servidor tem ligados', () => {
    expect(ai).toMatch(/>Para onde os dados vão</);
    expect(ai).toMatch(/\{aiAnalysisSummary\(aiDestinations\(status\)\)\}/);
    expect(ai).toMatch(/>Privacidade: o que é enviado e para onde</);
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
  const FLOWS_DIR = path.join(MOBILE_DIR, 'tests/e2e/flows');
  const flow = read('tests/e2e/flows/08-ia-assistente-direto.yaml');
  const shared = read('tests/e2e/flows/comum/conta-com-grupo-no-painel.yaml');
  const runner = fs.readFileSync(path.join(REPO_DIR, 'scripts/app-e2e-run-flows.sh'), 'utf8');
  const api = fs.readFileSync(path.join(REPO_DIR, 'scripts/app-e2e-api.sh'), 'utf8');
  /** Todos os fluxos, com as partes comuns. */
  const allFlows = sourceFiles(FLOWS_DIR).filter((file) => file.endsWith('.yaml'));
  /** Os rótulos de acessibilidade e os textos de uma tela, como o Maestro os enxerga. */
  const hasLabel = (screens: string, label: string) => screens.includes(`accessibilityLabel="${label}"`);

  it('o fluxo da IA está na lista dos que rodam, e a API de teste usa o provedor falso', () => {
    expect(runner).toContain('"08-ia-assistente-direto"');
    expect(runner).not.toContain('08-ia-ativar-e-assistente');
    expect(fs.existsSync(path.join(FLOWS_DIR, '08-ia-ativar-e-assistente.yaml'))).toBe(false);
    expect(api).toContain('Ai__UseFakeProvider=true');
  });

  it('nenhum fluxo espera, toca ou inclui a pergunta de ativação: a parte comum "responder Agora não" não existe mais', () => {
    expect(fs.existsSync(path.join(FLOWS_DIR, 'comum/responder-agora-nao-a-ia.yaml'))).toBe(false);
    expect(allFlows.length).toBeGreaterThanOrEqual(12);
    for (const file of allFlows) {
      const text = fs.readFileSync(file, 'utf8');
      const name = path.relative(FLOWS_DIR, file);
      expect([name, /responder-agora-nao-a-ia/.test(text)]).toEqual([name, false]);
      // Os passos (sem as linhas de comentário) que esperam ou tocam algo: nada da tela antiga, nada que só existia com aceite.
      const waitedOrTapped = text
        .split(/\r?\n/)
        .filter((line) => !line.trim().startsWith('#') && !/assertNotVisible/.test(line))
        .join('\n');
      expect([name, /Quer que a IA analise|Agora não ativar|Ativar a análise com IA|Entendi, manter|ativou a análise|Ativada por|Retirar o meu aceite|Análise com IA desligada/.test(waitedOrTapped)]).toEqual([name, false]);
    }
  });

  it('criar grupo e chegar ao Painel é direto, nas partes comuns e nos fluxos 01 e 09', () => {
    expect(shared).toMatch(/- tapOn: "Ir para o Painel"\s*- extendedWaitUntil:\s*visible: "Total de gastos"/);
    expect(read('tests/e2e/flows/01-cadastro-e-grupo.yaml')).toMatch(/- tapOn: "Ir para o Painel"\s*- extendedWaitUntil:\s*visible: "Total de gastos"/);
    expect(read('tests/e2e/flows/09-assinaturas-e-recorrencias.yaml')).toMatch(/- tapOn: "Entrar"\s*# Painel: o cartão das recorrências/);
  });

  it('fluxo 08: primeiro uso sem pergunta, Assistente direto; todo rótulo que ele toca existe numa tela, igual', () => {
    const screens = [read('app/(main)/index.tsx'), read('src/modules/chat/screens/ChatScreen.tsx')].join('\n');
    for (const label of ['Abrir o Assistente', 'Pergunta para o Assistente', 'Enviar mensagem', 'Voltar para o Painel']) {
      expect(flow).toContain(`"${label}"`);
      expect([label, hasLabel(screens, label)]).toEqual([label, true]);
    }
    // Do "Ir para o Painel" ao Assistente não há passo de IA no meio.
    expect(flow).toMatch(
      /- tapOn: "Ir para o Painel"\s*(#.*\s*)*- extendedWaitUntil:\s*visible: "Total de gastos"\s*timeout: 60000\s*- extendedWaitUntil:\s*visible: "Abrir o Assistente"/,
    );
    expect(flow).toContain('- assertNotVisible: "Quer que a IA analise as finanças do grupo\\\\?"');
    expect(flow).toContain('- assertNotVisible: "Análise com IA desligada.*"');
    expect(flow).toContain('- assertNotVisible: "A IA está desligada para este grupo"');
    expect(screens).toContain('title="A IA está desligada para este grupo"');
    // A resposta fixa do provedor falso da API (FakeLlmProvider).
    expect(flow).toContain('Esta é uma resposta de teste do assistente, sem dados reais.');
    const fake = fs.readFileSync(path.join(REPO_DIR, 'backend/src/CoupleSync.Infrastructure/Integrations/Llm/LlmProviders.cs'), 'utf8');
    expect(fake).toContain('Esta é uma resposta de teste do assistente, sem dados reais.');
  });

  it('fluxo 03 (duas contas no mesmo grupo): ninguém é perguntado; desligar e ligar de novo em Configurações > IA; todo rótulo e texto que ele toca existe numa tela', () => {
    const flow03 = read('tests/e2e/flows/03-entrar-em-grupo.yaml');
    const ai = read('app/(main)/settings/ai.tsx');
    const chat = read('src/modules/chat/screens/ChatScreen.tsx');
    const rules = read('src/modules/ai/aiStatus.ts');
    const screens = [read('app/(main)/index.tsx'), read('app/(main)/settings/index.tsx'), ai, chat].join('\n');
    for (const label of [
      'Abrir o Assistente',
      'Abrir a inteligência artificial: ligar ou desligar e ver o consumo',
      'Desligar a análise com IA para o grupo',
      'Ligar de novo a análise com IA para o grupo',
      'Voltar para as configurações',
      'Pergunta para o Assistente',
    ]) {
      expect(flow03).toContain(`"${label}"`);
      expect([label, hasLabel(screens, label)]).toEqual([label, true]);
    }
    // O botão do Assistente desligado: o rótulo vem pela propriedade do aviso.
    expect(flow03).toContain('- tapOn: "Abrir as configurações de inteligência artificial para ligar de novo"');
    expect(chat).toContain('actionAccessibilityLabel="Abrir as configurações de inteligência artificial para ligar de novo"');
    expect(chat).toMatch(/accessibilityLabel=\{actionAccessibilityLabel \?\? actionLabel\}/);
    expect(flow03).toContain('visible: "A IA está desligada para este grupo"');
    expect(chat).toContain('title="A IA está desligada para este grupo"');

    // Os estados que o fluxo espera são os textos da tela, inteiros (o Maestro compara o texto inteiro).
    expect(flow03.match(/visible: "Ligada"/g)).toHaveLength(2);
    expect(flow03.match(/visible: "Desligada para o grupo"/g)).toHaveLength(2);
    expect(rules).toContain("on: 'Ligada',");
    expect(rules).toContain("'off-for-group': 'Desligada para o grupo',");
    // Nenhum outro texto da tela é só "Ligada" ou só "Desligada para o grupo" (o aviso de sucesso tem outras palavras).
    expect(ai).toContain("'Análise com IA ligada para o grupo.'");
    expect(ai).toContain("'Análise com IA desligada para o grupo.'");
    expect(ai).not.toMatch(/[>'"]Ligada[<'"]/);

    // A confirmação de desligar: título e botão do aviso da tela.
    expect(flow03).toContain('"Desligar para o grupo\\\\?"');
    expect(ai).toContain("'Desligar para o grupo?'");
    expect(ai).toContain("text: 'Desligar',");

    // A ordem do que importa: entra no grupo -> Painel direto; desliga; Assistente desligado; liga de novo, direto.
    const order = [
      '- tapOn: "Confirmar entrada no grupo"',
      'visible: "Total de gastos"',
      'visible: "Ligada"',
      '- tapOn: "Desligar a análise com IA para o grupo"',
      'visible: "Desligada para o grupo"',
      'visible: "A IA está desligada para este grupo"',
      '- tapOn: "Ligar de novo a análise com IA para o grupo"',
      'visible: "Ligada"',
      '- tapOn: "Voltar para as configurações"',
    ];
    let from = 0;
    for (const step of order) {
      const at = flow03.indexOf(step, from);
      expect([step, at >= from && at !== -1]).toEqual([step, true]);
      from = at + step.length;
    }
    // Entre o toque de ligar de novo e o estado "Ligada" não há outro toque: é direto.
    const afterTurnOn = flow03.slice(flow03.indexOf('- tapOn: "Ligar de novo a análise com IA para o grupo"') + 10);
    expect(afterTurnOn.slice(0, afterTurnOn.indexOf('visible: "Ligada"'))).not.toMatch(/tapOn/);
  });

  it('os fluxos 01 e 06 rolam o Painel até o atalho das transações (um aviso acima pode empurrá-lo para baixo da dobra)', () => {
    for (const name of ['01-cadastro-e-grupo.yaml', '06-abas.yaml']) {
      const flow = read(`tests/e2e/flows/${name}`);
      expect(flow).toMatch(/- scrollUntilVisible:\s*element: "Ver todas as transações"\s*direction: DOWN/);
      expect(flow).not.toContain('- assertVisible: "Ver todas as transações"');
    }
  });

  it('nenhum texto novo do Painel ou das telas de IA casa com o que os fluxos afirmam que não aparece', () => {
    for (const file of ['app/(main)/index.tsx', 'app/(main)/settings/ai.tsx']) {
      expect(read(file)).not.toMatch(/Tentar novamente|Algo deu errado/);
    }
  });
});
