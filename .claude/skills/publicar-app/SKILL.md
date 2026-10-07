---
name: publicar-app
description: >-
  Publica o app do CoupleSync - atualização OTA de JavaScript (workflow mobile-update.yml) e, quando houve
  mudança nativa, APK novo por tag v* com Release no GitHub (mobile-apk.yml). Use como passos 12-13 da skill
  entregar, ou quando o dono pedir explicitamente para publicar o app, soltar um OTA ou gerar o APK do que já
  está em main. Não use antes de a API correspondente estar confirmada em produção.
argument-hint: "[ota | apk | ota+apk] [mensagem]"
---

# Publicar o app: $ARGUMENTS

Ordem fixa: **API confirmada → OTA → APK**. `CLAUDE.md` vale inteiro; as PARADAS, a notação `ghp` / `gitp`, a
regra das "Esperas longas" e a seção "Quem manda" da skill `entregar` valem aqui (conta pessoal no próprio
comando, nunca `gh auth switch`, negação de permissão = parar e relatar, comando interrompido = sem resultado).
Esta skill nunca reverte nada na API: fumaça com saída `1` ou `3` aqui é sempre parada, com o relato.

Fatos: projeto EAS `@luuty/couplesync`; uma linha de atualização, branch EAS `production`; os canais
`production` e `preview` apontam para ela e há APKs instalados nos dois — um OTA chega a TODOS os aparelhos.
`runtimeVersion` segue a política `sdkVersion`: o OTA só alcança APKs do mesmo SDK do Expo.

## 0. Pré-condições (todas, senão pare)

- **O commit a publicar** é um só, com o SHA completo (40 caracteres): a ponta de `origin/main` depois de
  `gitp fetch origin main` (`git rev-parse origin/main`). Na skill `entregar` ele tem de conter o commit do
  merge da entrega. Anote-o: todos os passos abaixo usam esse mesmo SHA, nunca "o que estiver em `main`".
- A API em produção já atende esse commit:
  `bash .claude/skills/entregar/scripts/producao-fumaca.sh <SHA>` com saída `0`. Qualquer outra saída → pare
  e relate. (Dentro da skill `entregar`, quem decide o que fazer com a saída é o passo 10 dela, antes de chegar aqui.)
- "Build & Test" verde nesse SHA — a execução mais recente, já terminada:
  `ghp api repos/lucasmoraiss/CoupleSync/commits/<SHA>/check-runs --jq '[.check_runs[] | select(.name=="Build & Test")] | sort_by(.started_at) | last | {status, conclusion}'`
  → `completed` / `success`. Ainda rodando: espere e consulte de novo. Outra conclusão, ou nenhuma execução → pare.
- O diff desde o último OTA não muda o SDK do Expo. O commit do último OTA é o `headSha` da última execução
  verde: `ghp run list --repo lucasmoraiss/CoupleSync --workflow mobile-update.yml --status success --limit 1 --json headSha`;
  compare com `git diff <headSha> <SHA> -- mobile/package.json` (linha `"expo":`). Mudou → PARADA: decisão do dono.
- Mudança nativa: o JavaScript novo guarda toda chamada nativa nova (o `revisor-final` conferiu) — o OTA vai
  chegar aos APKs antigos antes de qualquer pessoa instalar o novo.

## 1. OTA

1. Anote o grupo que está no ar, para a volta atrás. Com o EAS CLI local (logado como `luuty`):
   em `mobile/`, `npx eas-cli@12 update:list --branch production --limit 1 --json --non-interactive`.
   Sem EAS CLI na sessão: o id está no resumo/log da última execução verde de `mobile-update.yml`
   (linha `OTA_GROUP_ID=`); a primeira publicação por esta esteira pode não ter anterior registrado — diga isso no relato.
2. Dispare: `ghp workflow run mobile-update.yml --repo lucasmoraiss/CoupleSync --ref main -f commit=<SHA> -f message="<assunto do PR> (#<pr>)"`.
   O workflow confere, antes de empacotar, que roda em `main` e que a ponta de `main` é exatamente `<SHA>`;
   se `main` andou nesse meio-tempo a execução falha sem publicar nada. Nesse caso volte ao passo 0 com o
   commit novo (ele precisa ter passado pela esteira) — não dispare de novo às cegas.
3. Ache a execução (espere alguns segundos):
   `ghp run list --repo lucasmoraiss/CoupleSync --workflow mobile-update.yml --event workflow_dispatch --limit 1 --json databaseId,headSha,status`
   — `headSha` tem de ser `<SHA>`.
4. Acompanhe: `ghp run watch <id> --repo lucasmoraiss/CoupleSync --exit-status`.
5. Pegue o resultado: `ghp run view <id> --repo lucasmoraiss/CoupleSync --log` e procure `OTA_GROUP_ID=` e `OTA_BRANCH=`
   (o mesmo aparece no resumo da execução). `OTA_BRANCH` tem de ser `production`.
6. Falhou: leia `--log-failed`. Segredo ausente (`EXPO_TOKEN`, `EXPO_PUBLIC_API_BASE_URL`) → PARADA (é do dono).
   Erro de empacotamento → é defeito do código: volta para a esteira como correção. Não repita às cegas.

O OTA entra no aparelho na SEGUNDA abertura do app (abre, baixa, fecha por completo, abre de novo). Isso não
dá para verificar daqui e não segura a entrega: registre "OTA publicado; chegada ao aparelho não verificada" e
ponha o roteiro de teste na issue de checkpoint do dono (passo 14 da skill `entregar`).

## 2. APK (só com mudança nativa)

1. Versão: `expo.version` em `mobile/app.json` no `<SHA>` tem de ser maior que a da maior tag no formato exato
   `vX.Y.Z` (ignore tags com sufixo, como `v1.0.0-pit`)
   (o `implementador` sobe a versão quando mexe em nativo). Não subiu → PARADA: vira um PR de correção, nunca
   um commit direto em `main`.
2. Tag = `v<expo.version>`, no mesmo `<SHA>` do OTA. Confira que não existe:
   `gitp ls-remote --tags origin "refs/tags/v<versão>"` vazio. Existe → PARADA (nunca mover nem recriar tag).
3. `git tag -a v<versão> <SHA> -m "CoupleSync <versão>"` e `gitp push origin v<versão>`.
   O push da tag dispara `mobile-apk.yml` (perfil EAS `production`).
4. Acompanhe: `ghp run list --repo lucasmoraiss/CoupleSync --workflow mobile-apk.yml --limit 1 --json databaseId,headBranch,status`,
   depois `ghp run watch <id> --repo lucasmoraiss/CoupleSync --exit-status` (o build no EAS pode levar dezenas de minutos).
5. Confira a Release: `ghp release view v<versão> --repo lucasmoraiss/CoupleSync --json tagName,isDraft,isPrerelease,assets`
   → tem o arquivo `couplesync.apk`. Link fixo para os testers (sempre o mais novo):
   `https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk`
6. Falhou depois da tag criada: não apague a tag. Reexecute o workflow uma vez
   (`ghp run rerun <id> --failed`); falhou de novo → PARADA.

Instalar o APK e testar o que só existe nele (captura de notificações com o app fechado, reinício do aparelho)
só o dono consegue, nos checkpoints dele: registre como **não verificado** e ponha o roteiro na issue de checkpoint.

## 3. Volta atrás do OTA

Sintoma: o app novo quebra em uso. A API fica como está (o JavaScript anterior funciona com ela).
- Com o EAS CLI local (logado como `luuty`), em `mobile/`:
  `npx eas-cli@12 update:republish --group <id do grupo anterior> --message "volta atrás: <motivo>" --non-interactive`
- Sem EAS CLI na sessão, ou sem o id anterior → PARADA: peça ao dono para rodar o comando acima
  (os grupos aparecem em `eas update:list --branch production`).
- Depois conserte para a frente por PR; não publique outro OTA sem nova passagem pela esteira.
- APK: não apague Release nem tag; relate e deixe a decisão com o dono.

## Relato

Tipo publicado (OTA / APK); commit; id do grupo OTA novo e do anterior; tag e link da Release; o que NÃO foi
verificado em aparelho e o roteiro curto de teste para o dono.
