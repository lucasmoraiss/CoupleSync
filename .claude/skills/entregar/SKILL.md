---
name: entregar
description: >-
  Esteira de entrega autônoma do CoupleSync. Leva UM item do backlog (número de issue do GitHub ou tarefa em
  texto) da implementação até produção — implementar, verificar, revisar, corrigir, polir, revisão final, PR,
  merge com CI verde, deploy confirmado, OTA/APK e fechamento da issue. Use só quando o dono pedir
  explicitamente para entregar, implementar e publicar um item (por exemplo "entregue a issue 7" ou
  "/entregar 12"). Não use para tirar dúvida, investigar, ou mexer em código sem publicar.
argument-hint: "<nº da issue | descrição da tarefa>"
---

# Entregar: $ARGUMENTS

Você é o **controlador** desta entrega. Você não escreve código de produto: despacha os papéis de
`.claude/agents/`, roda as verificações você mesmo, decide com base em evidência e para nas condições de PARADA.
`CLAUDE.md` vale inteiro. Siga os passos na ordem; não pule nem funda passos.

**Notação.** `ghp` = `GH_TOKEN=$(gh auth token --user lucasmoraiss) gh` (sempre com `--repo lucasmoraiss/CoupleSync`).
`gitp` = `GH_TOKEN=$(gh auth token --user lucasmoraiss) git -c credential.helper= -c 'credential.helper=!f() { echo username=lucasmoraiss; echo password=$GH_TOKEN; }; f'`.
Escreva o comando completo a cada chamada (o shell não guarda estado). Nunca `gh auth switch`. Nunca imprima o token.

**Pasta de trabalho** (ignorada pelo git): `.agents-work/entrega/<slug>/` — `tarefa.md`, `relatorio.md`,
`verificacao.md`, arquivos `*.diff`, `revisao-N.md`. Nada dali entra em commit.

## PARADAS — o que a esteira nunca faz sozinha

Em qualquer uma delas: pare na hora, não tente outro caminho, deixe o branch como está e faça o relato de
parada (formato no fim). Só o dono libera.

1. **Dado de produção em risco**: qualquer coisa destrutiva ou sem volta sobre dados de produção sem (a) passo
   de backup dentro da própria migration e (b) teste em `CoupleSync.PostgresTests` passando sobre dados no
   formato antigo. Mesmo com os dois, a primeira publicação de uma conversão sem volta espera o "pode" do dono.
2. **Migration não aditiva**: remove ou renomeia coluna/tabela, muda tipo com perda, apaga linhas.
3. **Suíte obrigatória falhando ou pulada**, localmente ou no CI. "Build & Test" vermelho, cancelado ou ausente
   não é verde. Nunca desligar, afrouxar ou pular teste para passar; nunca `--no-verify`.
4. **Segredo no diff** (chave, token, senha, connection string, `.env*`, `appsettings.*.json` que não seja o
   `appsettings.json`, `google-services.json`) ou dado pessoal/bancário real.
5. **Negação do sistema de permissões** em qualquer comando: pare e relate o comando negado. Nunca contorne
   (outro comando, outra conta, outra ferramenta, pedir a um subagente).
6. **`revisor-final` disse NÃO PODE PUBLICAR** e a rodada de correção permitida (passo 7) não resolveu.
7. **Teto de rodadas de correção atingido** (passo 5).
8. **Decisão que é do dono**: requisito ambíguo que muda o resultado; variável nova no Render; mudança de SDK
   do Expo (muda o `runtimeVersion` e deixa os APKs instalados sem OTA); qualquer configuração do GitHub,
   Render, Expo ou Neon; tag que já existe; `main` que andou com conflito que não é trivial.
9. **Deploy não confirmado**: `/health` não mostra o commit do merge no prazo, ou a fumaça de produção falha
   (ver "Volta atrás").

## Passos

### 1. Escolher e esclarecer o item

- Número: `ghp issue view <n> --repo lucasmoraiss/CoupleSync --json number,title,body,labels,state,comments`.
  Precisa estar aberta e ter o rótulo `backlog`; se não, pergunte ao dono antes de seguir.
- Texto livre: é a tarefa. Sem issue, o fechamento (passo 14) vira relato ao dono.
- Sem argumento: liste `ghp issue list --label backlog --state open` e pergunte qual.
- Escreva `tarefa.md`: objetivo, critérios de aceite verificáveis, fora do escopo, áreas prováveis
  (`backend/`, `mobile/`, nativo), riscos conhecidos (migration? contrato? dependência?).
- Ambiguidade que muda o resultado → PARADA 8 com a pergunta exata. Ambiguidade pequena: decida, registre em
  `tarefa.md` e siga.

### 2. Branch

- Árvore limpa (`git status --porcelain` vazio) — senão pare e pergunte.
- `gitp fetch origin main`, depois `git switch -c entrega/<n>-<slug> origin/main` (sem issue: `entrega/<slug>`).

### 3. Implementação

Despache `implementador` com: caminho de `tarefa.md`, caminho de `relatorio.md`. Modelo: o do agente; passe
`model: opus` no despacho quando a tarefa for de arquitetura (camada nova, esquema de dados, sessão/autorização,
mudança que atravessa API e app).
- `BLOQUEADO` / `PRECISA_DE_CONTEXTO`: resolva você se a resposta estiver no repositório ou na issue e
  despache de novo; senão PARADA 8.

### 4. Barreira de verificação (você roda; não vale o que o implementador disse)

Rode na ponta do branch e grave comandos e saídas em `verificacao.md`:

| Verificação | Quando |
| --- | --- |
| `dotnet test backend/CoupleSync.sln` com `DATABASE_URL` local e `COUPLESYNC_REQUIRE_POSTGRES_TESTS=1` | sempre |
| `dotnet ef migrations has-pending-model-changes` (comando completo no `CLAUDE.md`) | sempre |
| `npx tsc --noEmit` e `npm test`, em `mobile/` | sempre |
| `docker build` + `scripts/api-image-smoke-test.sh` normal e com `SMOKE_FORCE_INVARIANT=1` | o diff toca `backend/` |
| `npx expo prebuild --no-install --platform android` em cópia temporária fora do repositório (só arquivos versionados; sem `.env*` e sem `google-services.json`), apagada depois | o diff toca `mobile/package.json`, `mobile/package-lock.json`, `mobile/app.json` ou `mobile/plugins/` |
| Varredura de segredos e de dados reais no diff (`git diff origin/main...HEAD`) | sempre |
| Migrations do diff: aditivas? backup antes de conversão sem volta? teste PostgreSQL de dados antigos? | o diff tem migration |

- Sem Docker na sessão: se o diff NÃO toca `backend/`, registre "PostgreSQL e imagem: delegados ao CI" e siga
  (o CI roda os dois como obrigatórios). Se toca `backend/` → PARADA 3.
- Falhou: devolva ao `implementador` com a saída (conta como rodada de correção do passo 5). Nunca conserte você.
- Segredo → PARADA 4. Migration não aditiva → PARADA 2 (ou 1).

### 5. Revisão e rodadas de correção

1. Gere `revisao-1.diff` (`git log --oneline origin/main..HEAD`, `git diff --stat origin/main...HEAD`,
   `git diff origin/main...HEAD`) e despache `revisor` com `tarefa.md`, `relatorio.md`, o diff e `verificacao.md`.
2. `APROVADO` → passo 6. `MUDANÇAS NECESSÁRIAS` → rodada de correção:
   - despache `implementador` (rodada de correção) com TODOS os achados Críticos e Importantes, e os Menores
     que forem baratos; os outros Menores vão para a lista de adiados;
   - repita o passo 4 nas áreas tocadas;
   - gere o diff só da correção (`git diff <ponta-antes>..HEAD`) e despache `re-revisor` com a lista de achados,
     esse diff e `relatorio.md`.
3. `RESTAM ACHADOS` ou defeito novo → outra rodada.
4. **Teto: 3 rodadas de correção** nesta etapa. Na terceira que termina sem `TODOS RESOLVIDOS` → PARADA 7:
   nada de push; comente na issue o que está pronto e a lista exata do que resta; relate ao dono.
5. Depois de `TODOS RESOLVIDOS`, se as correções somadas mudaram mais do que os trechos apontados (arquivo
   novo, contrato, migration), rode o `revisor` de novo no diff inteiro — uma vez; o que ele achar entra no mesmo teto.

Você não rebaixa achado, não discute gravidade com o revisor e não aprova no lugar dele.

### 6. Polimento

Despache `polidor` com a lista de arquivos alterados (`git diff --name-only origin/main...HEAD`), o intervalo de
commits, os Menores opcionais e `relatorio.md`.
- `POLIDO`: confira que o diff do polimento só toca arquivos da lista e nada da lista proibida dele; rode o
  passo 4 de novo. Qualquer problema: `git revert` dos commits de polimento (commit novo, sem reescrever) e siga sem eles.
- `NADA_A_POLIR` / `RECUSADO`: siga.

### 7. Revisão final

Gere `final.diff` (branch inteiro contra `origin/main`) e despache `revisor-final` com ele, `tarefa.md`,
`relatorio.md`, as revisões, `verificacao.md` e os Menores adiados.
- `PODE PUBLICAR` → passo 8, levando a "Lista de publicação" dele.
- `NÃO PODE PUBLICAR` → UMA rodada: `implementador` corrige os Críticos/Importantes, passo 4, `re-revisor`
  confere, e o `revisor-final` roda de novo. Segundo `NÃO PODE PUBLICAR` → PARADA 6.
- Se a lista de publicação pede algo do dono ANTES do merge (variável no Render, backup, consulta) → PARADA 8
  com a lista; retome quando ele confirmar.

### 8. Push e PR

- `gitp push -u origin entrega/<...>`
- `ghp pr create --repo lucasmoraiss/CoupleSync --base main --head entrega/<...> --title "<tipo(area): assunto>" --body-file <arquivo>`
  Corpo, em português: o que muda e por quê; `Refs #<n>` (NÃO use `Closes`/`Fixes`: a issue só fecha no passo 14);
  evidência da verificação (totais); veredito das revisões; tipo da mudança (API / JavaScript / nativa);
  migrations; lista de publicação; Menores adiados; o que não foi verificado.

### 9. Esperar o CI e fazer o merge

- SHA da ponta: `git rev-parse HEAD`. Espere a verificação **"Build & Test"** desse SHA aparecer e terminar:
  `ghp pr checks <pr> --repo lucasmoraiss/CoupleSync --watch --interval 30`, depois confirme
  `ghp pr checks <pr> --repo lucasmoraiss/CoupleSync --json name,bucket` → `Build & Test` com `bucket` = `pass`.
- Falhou: leia `ghp run view <id> --log-failed`; defeito do código → rodada de correção (passos 4–5, mesmo teto)
  e novo push; instabilidade do CI → UMA reexecução (`ghp run rerun <id> --failed`); falhou de novo → PARADA 3.
- A revisão automática "Claude — revisão do PR" não é obrigatória, mas leia o comentário dela: achado Crítico
  ou Importante que procede entra como rodada de correção.
- Merge: `ghp pr merge <pr> --repo lucasmoraiss/CoupleSync --merge --match-head-commit <sha>`.
  Recusado por proteção de branch (ex.: branch desatualizado): traga `origin/main` para o branch com um merge
  (sem rebase, sem force-push), refaça o passo 4 e este passo. Nunca use `--admin`.
- Commit do merge: `ghp pr view <pr> --repo lucasmoraiss/CoupleSync --json mergeCommit --jq .mergeCommit.oid`.

### 10. Confirmar o deploy da API

`bash .claude/skills/entregar/scripts/producao-fumaca.sh <commit-do-merge>` — espera `/health` mostrar o commit
(até 20 min), confere `/health/live`, `/health/ready` e o formato de erro num 401, e, **se houver conta de
demonstração**, entra com ela e faz só leituras. O script só usa `GET` e o `POST` de login da conta de
demonstração; nunca cria, altera ou apaga dado.
- Conta de demonstração: variáveis `COUPLESYNC_DEMO_EMAIL` e `COUPLESYNC_DEMO_PASSWORD`, ou o arquivo local
  fora do repositório `~/.couplesync/conta-demo` (duas linhas `CHAVE=valor`). O script lê; você não abre nem
  imprime. Nunca no repositório. Sem conta: a parte autenticada fica registrada como **não verificada**.
- Nunca use conta de usuário real nem crie conta nova em produção para testar.
- O serviço do Render tem `rootDir: backend` (`render.yaml`). Se o merge NÃO toca `backend/`, a API publicada
  não muda e `/health` pode continuar no commit anterior: rode o script SEM argumento (só a fumaça) e registre a
  versão que ele mostrar. Se toca `backend/`, o commit do merge em `/health` é obrigatório.
- Falha → "Volta atrás" e PARADA 9.

### 11. Classificar a mudança (pelo diff do merge, conferindo com o `revisor-final`)

- **Nativa**: toca `mobile/android-native/`, `mobile/plugins/`, `mobile/android/`, configuração nativa em
  `mobile/app.json` (plugins, permissões, pacote, ícone, `scheme`, `updates`, `runtimeVersion`) ou dependência
  com código nativo em `mobile/package.json`. → OTA **e** APK.
- **JavaScript**: qualquer outra mudança que entra no pacote do app (`mobile/app/`, `mobile/src/` fora de
  testes, `mobile/assets/`, dependência só de JavaScript). → OTA.
- **Só API**: nada disso. → sem publicação do app.

### 12–13. Publicar o app

JavaScript ou nativa: siga a skill `publicar-app` (OTA; depois APK se nativa). Sempre depois do passo 10 verde.

### 14. Fechar

- `ghp issue comment <n> --repo lucasmoraiss/CoupleSync --body-file <arquivo>` com: PR e commit do merge; versão
  em `/health`; migrations aplicadas; OTA (id do grupo) e APK (tag e link) quando houver; resultado da fumaça;
  **o que ficou sem verificação** (ex.: fluxo em aparelho real, e-mail, parte autenticada sem conta de
  demonstração) com o roteiro curto para o dono testar; Menores adiados.
- Cada Menor adiado que vale a pena vira issue com rótulo `backlog` (`ghp issue create`), citada no comentário.
- `ghp issue close <n> --repo lucasmoraiss/CoupleSync`. Volte para `main` local (`git switch main`), sem apagar nada do dono.
- Relato final ao dono: o mesmo conteúdo, curto.

## Volta atrás

- **API** (fumaça falhou depois do deploy; repita a fumaça uma vez após 2 min antes de concluir que falhou):
  - Mudança SEM migration: abra o PR de reversão você mesmo — branch `reverte/<pr>` a partir de `origin/main`,
    `git revert -m 1 <commit-do-merge>`, push, PR, "Build & Test" verde, merge, e confirme `/health` com o commit
    da reversão. Não publique OTA. Depois PARADA 9 com o relato.
  - Mudança COM migration: não reverta sozinho. PARADA 9 com o diagnóstico e a proposta. **Migrations nunca são
    revertidas** nem apagadas do histórico do banco; o conserto é sempre para a frente.
- **OTA**: `eas update:republish` do grupo anterior (procedimento na skill `publicar-app`). A API fica na
  versão nova; o JavaScript anterior funciona com ela (é para isso que o contrato é compatível).
- **APK**: não apague Release nem tag. Relate; o dono decide. O APK anterior continua instalado em quem não atualizou.
- Nunca: force-push em `main`, apagar/mover tag, mexer no banco, "consertar" produção por fora do PR.

## Relato de parada (formato)

```
PARADA <nº e nome>
Item: #<n> <título> · branch: <nome> · último passo concluído: <nº>
O que aconteceu: <fato + evidência: comando, saída, arquivo:linha>
Estado: <o que está commitado / enviado / publicado; o que NÃO foi feito>
Preciso de você para: <decisão ou ação exata, com as opções>
Para retomar: <de qual passo>
```
