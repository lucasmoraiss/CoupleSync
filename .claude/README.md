# Esteira de entrega autônoma — guia do dono

Você gerencia; os agentes desenvolvem. Em qualquer sessão do Claude Code neste repositório (local ou na nuvem):

```
/entregar 7            ← número de uma issue com o rótulo backlog
/entregar <descrição>  ← tarefa em texto, sem issue
/publicar-app          ← só OTA / APK do que já está em main
```

Também funciona pedir por extenso ("entregue a issue 7").

## O que acontece

issue → branch `entrega/<n>-...` → `implementador` → verificação local (todas as suítes, migrations, imagem,
prebuild quando preciso) → `revisor` → correções com `re-revisor` (até 3 rodadas) → `polidor` → `revisor-final`
→ push e PR → CI "Build & Test" verde → merge → espera `/health` mostrar o commit → fumaça de produção
(só leitura) → OTA se mudou JavaScript → tag e APK se mudou nativo → comentário na issue com o que foi
publicado e **o que ficou sem verificação** → issue fechada.

| Arquivo | Para quê |
| --- | --- |
| `CLAUDE.md` | Regras permanentes, carregadas em toda sessão |
| `.claude/agents/*.md` | Os papéis: `implementador`, `revisor`, `re-revisor`, `polidor`, `revisor-final` |
| `.claude/skills/entregar/` | A esteira, as PARADAS e a volta atrás |
| `.claude/skills/publicar-app/` | OTA e APK |
| `.github/workflows/claude-review.yml` | Revisão automática de todo PR (desligada até existir o segredo) |

## O que você faz uma vez, à mão (nesta ordem)

1. **Proteção do branch `main`** — GitHub → Settings → Branches → *Add branch ruleset* (ou regra clássica)
   para `main`:
   - *Require a pull request before merging* — aprovações exigidas: **0** (os PRs saem da sua própria conta;
     o GitHub não deixa o autor aprovar o próprio PR);
   - *Require status checks to pass* → adicione **`Build & Test`** e marque *Require branches to be up to date*;
   - *Block force pushes* e *Restrict deletions* ligados; sem lista de exceção (vale para administradores).
   - NÃO adicione "Claude review" como obrigatória.
   Os agentes não fazem isto: a tentativa foi negada pelo sistema de permissões, e a regra é não contornar.
2. **Revisão automática de PR** — gere um token com `claude setup-token` e grave como segredo do repositório
   `CLAUDE_CODE_OAUTH_TOKEN` (Settings → Secrets and variables → Actions). Alternativa: uma chave da API em
   `ANTHROPIC_API_KEY` (use só um dos dois). O workflow comenta com o `GITHUB_TOKEN` do próprio GitHub Actions,
   então instalar o app Claude do GitHub (`/install-github-app` ou https://github.com/apps/claude) é opcional —
   ele só é necessário se você quiser responder a `@claude` em issues e PRs. Se rodar `/install-github-app`,
   não aceite o workflow de revisão que ele oferece: este repositório já tem o seu.
3. **E-mail (Brevo) no Render** — serviço `couplesync-api` → Environment: `Email__ApiKey` (chave da Brevo) e
   `Email__FromAddress` (remetente confirmado na Brevo). `Email__Provider=brevo` e `Email__FromName` já vêm do
   `render.yaml`. Sem as duas, a API sobe e o envio de e-mail fica desligado (503 `EMAIL_NOT_CONFIGURED`).
4. **Conta de demonstração** (opcional, melhora a fumaça de produção) — crie pelo app uma conta cujo e-mail
   contenha `demo`, coloque-a num grupo só dela e guarde as credenciais FORA do repositório: variáveis
   `COUPLESYNC_DEMO_EMAIL` / `COUPLESYNC_DEMO_PASSWORD`, ou o arquivo `~/.couplesync/conta-demo` com essas duas
   linhas `CHAVE=valor`. Sem ela a esteira confere só as rotas públicas e avisa que o resto não foi verificado.

Já está pronto: segredos `EXPO_TOKEN` e `EXPO_PUBLIC_API_BASE_URL`, merge automático e apagar branch no merge.

## Quando a esteira para e o que ela pergunta

Ela nunca decide estas coisas sozinha. Você recebe um relato com o que aconteceu, o estado e a pergunta exata.

| PARADA | O que você decide |
| --- | --- |
| Dado de produção em risco / migration não aditiva | Se a mudança de dados vale; se o backup e o teste em PostgreSQL bastam; quando publicar |
| Suíte falhando ou pulada (local ou CI) | Nada a decidir se for defeito — ela volta a corrigir; você é chamado se persistir |
| Segredo ou dado real no diff | Trocar o segredo se ele chegou a sair da máquina |
| Permissão negada | Liberar o comando, fazer você mesmo, ou mudar o plano |
| `revisor-final`: NÃO PODE PUBLICAR | Aceitar o adiamento, mudar o escopo, ou liberar mais uma rodada |
| Teto de 3 rodadas de correção | Idem |
| Decisão sua | Requisito ambíguo; variável nova no Render; mudança de SDK do Expo; tag que já existe |
| Deploy não confirmado / fumaça falhou | Olhar o log do Render; confirmar a volta atrás quando há migration |

## O que só você consegue verificar

A esteira diz isto no fechamento de cada issue: chegada do OTA ao aparelho (entra na segunda abertura do app),
instalação e uso do APK novo, captura de notificações de banco, recebimento de e-mail, leitura de extratos reais.
Link fixo do APK mais novo: https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk

## Volta atrás

API: PR de reversão (a esteira abre sozinha quando não há migration). OTA: `eas update:republish` do grupo
anterior. Migrations nunca são revertidas — o conserto é sempre para a frente.
