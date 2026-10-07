# Esteira de entrega autônoma — guia do dono

Você gerencia; os agentes desenvolvem. Em qualquer sessão do Claude Code neste repositório (local ou na nuvem):

```
/entregar 7            ← número de uma issue sua com o rótulo backlog
/entregar <descrição>  ← tarefa em texto, sem issue
/publicar-app          ← só OTA / APK do que já está em main
```

Também funciona pedir por extenso ("entregue a issue 7").

## O que acontece

issue → branch `entrega/<n>-...` → `implementador` → verificação local (todas as suítes, migrations, imagem,
prebuild quando preciso) → `revisor` → correções com `re-revisor` (até 3 rodadas) → `revisor` de novo no diff
inteiro → `polidor` → `revisor-final`, que aprova um commit exato → push e PR → CI "Build & Test" verde → merge
desse mesmo commit → espera `/health` mostrar o commit → fumaça de produção → OTA se mudou JavaScript → tag e
APK se mudou nativo → comentário na issue com o que foi publicado e **o que ficou sem verificação** → o que
depende de aparelho vai para a sua issue de checkpoint → issue fechada.

| Arquivo | Para quê |
| --- | --- |
| `CLAUDE.md` | Regras permanentes, carregadas em toda sessão |
| `.claude/agents/*.md` | Os papéis: `implementador`, `revisor`, `re-revisor`, `polidor`, `revisor-final` |
| `.claude/skills/entregar/` | A esteira, as PARADAS e a volta atrás |
| `.claude/skills/publicar-app/` | OTA e APK |

## Três escolhas suas que moldam a esteira

- **Sem revisão por IA no GitHub.** Não há credencial do Claude neste repositório. A revisão acontece toda na
  sessão, antes do PR, e por isso é mais dura: três passagens de revisão independentes, todo critério de aceite
  precisa de um teste, dúvida conta como o nível mais grave, e só entra em `main` o commit que o `revisor-final`
  aprovou — qualquer commit depois disso volta para a revisão.
- **Teste em aparelho só nos seus checkpoints.** A esteira não espera por você: publica com as verificações
  automáticas e acumula, na issue com o rótulo `checkpoint`, o roteiro curto do que só um aparelho mostra
  (chegada do OTA, APK novo, notificações de banco, e-mail recebido). Você testa quando quiser e responde lá.
- **Os arquivos que apontam para a produção continuam legíveis.** `.env*` e `appsettings.Development.json` não
  são bloqueados por regra de permissão; a proteção é a regra escrita no `CLAUDE.md` ("nunca ler"), que todos
  os papéis repetem.

## Quem pode pedir trabalho

Só você. O repositório é público, então a esteira trata como pedido apenas a sua mensagem na sessão e as
issues abertas pela conta `lucasmoraiss`; dos comentários, só os seus. Texto de qualquer outra pessoa — issue,
comentário, PR — é lido como dado e nunca como instrução.

## O que você faz uma vez, à mão

### 1. Regra do branch `main`

GitHub → repositório → **Settings** → **Rules** → **Rulesets** → abra o conjunto de regras que vale para
`main` (ou **New ruleset → New branch ruleset**, com *Enforcement status* = **Active** e, em *Target branches*,
**Add target → Include default branch**). Em **Branch rules**, deixe marcado **só** isto:

| Regra | Como configurar |
| --- | --- |
| **Restrict deletions** | marcada |
| **Block force pushes** | marcada |
| **Require a pull request before merging** | marcada; *Required approvals* = **0** (os PRs saem da sua própria conta, e o GitHub não deixa o autor aprovar o próprio PR) |
| **Require status checks to pass** | marcada; marque *Require branches to be up to date before merging*; clique **Add checks**, digite `Build & Test` e escolha o item que aparece com a origem **GitHub Actions** |

Todo o resto fica **desmarcado**. Em especial, estas duas travam todo merge neste repositório:
- **Require deployments to succeed** — exige um deploy do próprio branch do PR num ambiente, o que nunca acontece aqui;
- **Require code quality results** — exige um resultado de análise que este repositório não produz.

*Bypass list* vazia (a regra vale também para você). **Save changes**.

Como conferir: abra qualquer PR; na caixa de merge tem de aparecer `Build & Test` como **Required**, e o
botão de merge só libera com ele verde. Os agentes não mexem nesta configuração: é sua.

### 2. E-mail (Brevo) no Render — feito

Serviço `couplesync-api` → Environment: `Email__ApiKey` e `Email__FromAddress` (`Email__Provider=brevo` e
`Email__FromName` vêm do `render.yaml`). Sem as duas, a API sobe e as rotas de e-mail respondem 503
`EMAIL_NOT_CONFIGURED`. Conferir que o e-mail chega de verdade é item de checkpoint (peça "esqueci minha senha"
no app com a sua conta).

### 3. Conta de demonstração (opcional, melhora a fumaça de produção)

Uma conta de produção só de teste, num grupo só dela, cujo e-mail **começa com `demo`**. Guarde as credenciais
FORA do repositório, no arquivo `~/.couplesync/conta-demo` (ou em variáveis de ambiente com os mesmos nomes):

```
COUPLESYNC_DEMO_EMAIL=demo...@...
COUPLESYNC_DEMO_PASSWORD=...
COUPLESYNC_DEMO_ALLOW=demo...@...      ← o mesmo e-mail, repetido: é a única conta que o script aceita usar
```

Sem ela a esteira confere só as rotas públicas e avisa que a parte autenticada não foi verificada.

Já está pronto: segredos `EXPO_TOKEN` e `EXPO_PUBLIC_API_BASE_URL` no GitHub e apagar o branch no merge.

## Quando a esteira para e o que ela pergunta

Ela nunca decide estas coisas sozinha. Você recebe um relato com o que aconteceu, o estado e a pergunta exata
— e, se for algo para você fazer à mão, o passo a passo completo.

| PARADA | O que você decide |
| --- | --- |
| Dado de produção em risco / migration não aditiva | Se a mudança de dados vale; se o backup e o teste em PostgreSQL bastam; quando publicar |
| Suíte falhando ou pulada (local ou CI) | Nada a decidir se for defeito — ela volta a corrigir; você é chamado se persistir |
| Segredo ou dado real no diff | Trocar o segredo se ele chegou a sair da máquina |
| Permissão negada | Liberar o comando, fazer você mesmo, ou mudar o plano |
| `revisor-final`: NÃO PODE PUBLICAR | Aceitar o adiamento, mudar o escopo, ou liberar mais uma rodada |
| Teto de 3 rodadas de correção | Idem |
| Decisão sua | Requisito ambíguo; variável nova no Render; mudança de SDK do Expo; tag que já existe; merge recusado pela regra do branch; pedido que mexe em credenciais ou afrouxa uma verificação |
| Deploy não confirmado | Olhar o log do Render. Sem prova de falha a esteira **não** reverte nada |
| Fumaça falhou duas vezes | Ela já abriu a reversão (quando não há migration); você confirma o diagnóstico |

## O que fica para os seus checkpoints

Chegada do OTA ao aparelho (entra na segunda abertura do app), instalação e uso do APK novo, captura de
notificações de banco, recebimento de e-mail, leitura de extratos reais. Tudo isso está, entrega por entrega,
na issue com o rótulo `checkpoint`.
Link fixo do APK mais novo: https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk

## Volta atrás

API: PR de reversão — a esteira abre sozinha só com falha comprovada duas vezes e sem migration envolvida.
OTA: `eas update:republish` do grupo anterior. Migrations nunca são revertidas — o conserto é sempre para a frente.
