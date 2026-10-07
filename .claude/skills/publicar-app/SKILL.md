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

Ordem fixa: **API confirmada → OTA → APK**. `CLAUDE.md` vale inteiro; as PARADAS e a notação `ghp` / `gitp`
da skill `entregar` valem aqui (conta pessoal no próprio comando, nunca `gh auth switch`, negação de permissão
= parar e relatar).

Fatos: projeto EAS `@luuty/couplesync`; uma linha de atualização, branch EAS `production`; os canais
`production` e `preview` apontam para ela e há APKs instalados nos dois — um OTA chega a TODOS os aparelhos.
`runtimeVersion` segue a política `sdkVersion`: o OTA só alcança APKs do mesmo SDK do Expo.

## 0. Pré-condições (todas, senão pare)

- O que vai ser publicado está em `main` e é o que a API em produção já atende:
  `bash .claude/skills/entregar/scripts/producao-fumaca.sh <commit de main>` verde (ou, se o commit não tocou
  `backend/`, a fumaça sem argumento verde).
- "Build & Test" verde nesse commit.
- O diff desde o último OTA não muda o SDK do Expo (`expo` em `mobile/package.json`). Mudou → PARADA: decisão do dono.
- Mudança nativa: o JavaScript novo guarda toda chamada nativa nova (o `revisor-final` conferiu) — o OTA vai
  chegar aos APKs antigos antes de qualquer pessoa instalar o novo.

## 1. OTA

1. Anote o grupo que está no ar, para a volta atrás. Com o EAS CLI local (logado como `luuty`):
   em `mobile/`, `npx eas-cli@12 update:list --branch production --limit 1 --json --non-interactive`.
   Sem EAS CLI na sessão: o id está no resumo/log da última execução verde de `mobile-update.yml`
   (linha `OTA_GROUP_ID=`); a primeira publicação por esta esteira pode não ter anterior registrado — diga isso no relato.
2. Dispare: `ghp workflow run mobile-update.yml --repo lucasmoraiss/CoupleSync --ref main -f message="<assunto do PR> (#<pr>)"`.
3. Ache a execução (espere alguns segundos):
   `ghp run list --repo lucasmoraiss/CoupleSync --workflow mobile-update.yml --event workflow_dispatch --limit 1 --json databaseId,headSha,status`
   — confira que `headSha` é o commit de `main` que você quer.
4. Acompanhe: `ghp run watch <id> --repo lucasmoraiss/CoupleSync --exit-status`.
5. Pegue o resultado: `ghp run view <id> --repo lucasmoraiss/CoupleSync --log` e procure `OTA_GROUP_ID=` e `OTA_BRANCH=`
   (o mesmo aparece no resumo da execução). `OTA_BRANCH` tem de ser `production`.
6. Falhou: leia `--log-failed`. Segredo ausente (`EXPO_TOKEN`, `EXPO_PUBLIC_API_BASE_URL`) → PARADA (é do dono).
   Erro de empacotamento → é defeito do código: volta para a esteira como correção. Não repita às cegas.

O OTA entra no aparelho na SEGUNDA abertura do app (abre, baixa, fecha por completo, abre de novo). Isso não
dá para verificar daqui: registre "OTA publicado; chegada ao aparelho não verificada" e o roteiro de teste.

## 2. APK (só com mudança nativa)

1. Versão: `expo.version` em `mobile/app.json` de `main` tem de ser maior que a da última tag `v*`
   (o `implementador` sobe a versão quando mexe em nativo). Não subiu → PARADA: vira um PR de correção, nunca
   um commit direto em `main`.
2. Tag = `v<expo.version>`, no commit do merge. Confira que não existe:
   `gitp ls-remote --tags origin "refs/tags/v<versão>"` vazio. Existe → PARADA (nunca mover nem recriar tag).
3. `git tag -a v<versão> <commit-do-merge> -m "CoupleSync <versão>"` e `gitp push origin v<versão>`.
   O push da tag dispara `mobile-apk.yml` (perfil EAS `production`).
4. Acompanhe: `ghp run list --repo lucasmoraiss/CoupleSync --workflow mobile-apk.yml --limit 1 --json databaseId,headBranch,status`,
   depois `ghp run watch <id> --repo lucasmoraiss/CoupleSync --exit-status` (o build no EAS pode levar dezenas de minutos).
5. Confira a Release: `ghp release view v<versão> --repo lucasmoraiss/CoupleSync --json tagName,isDraft,isPrerelease,assets`
   → tem o arquivo `couplesync.apk`. Link fixo para os testers (sempre o mais novo):
   `https://github.com/lucasmoraiss/CoupleSync/releases/latest/download/couplesync.apk`
6. Falhou depois da tag criada: não apague a tag. Reexecute o workflow uma vez
   (`ghp run rerun <id> --failed`); falhou de novo → PARADA.

Instalar o APK e testar o que só existe nele (captura de notificações com o app fechado, reinício do aparelho)
só o dono consegue: registre como **não verificado**, com o roteiro.

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
