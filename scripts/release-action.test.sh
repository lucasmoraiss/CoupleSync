#!/usr/bin/env bash
# Prova de scripts/release-action.sh sem GitHub: cada caso monta o JSON de `gh release view --json isDraft,assets`.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="${HERE}/release-action.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
fail=0

# caso <nome> <palavra esperada> <precisa enviar o arquivo: sim|nao> <json, ou @ausente, ou @vazio>
caso() {
  local nome="$1" esperado="$2" envia="$3" json="$4" arg=""
  rm -f "${TMP}/r.json"
  case "${json}" in
    @ausente) arg="${TMP}/nao-existe.json" ;;
    @vazio)   : > "${TMP}/r.json"; arg="${TMP}/r.json" ;;
    *)        printf '%s' "${json}" > "${TMP}/r.json"; arg="${TMP}/r.json" ;;
  esac
  local saida rc saida2
  saida="$(bash "${SCRIPT}" "${arg}" 2>&1)"; rc=$?
  saida2="$(bash "${SCRIPT}" "${arg}" --arquivo 2>&1)"
  if [ "${rc}" -ne 0 ] || [ "${saida}" != "${esperado}" ] || [ "${saida2}" != "${envia}" ]; then
    echo "FALHA ${nome}: esperado '${esperado}'/'${envia}', veio '${saida}'/'${saida2}' (exit ${rc})"; fail=1
  else
    echo "OK    ${nome} -> ${saida} (enviar arquivo: ${saida2})"
  fi
}

APK='{"name":"couplesync.apk","state":"uploaded"}'
caso "release inexistente (arquivo ausente)" criar     sim '@ausente'
caso "release inexistente (saida vazia)"     criar     sim '@vazio'
caso "publicada com o arquivo"               nada      nao "{\"isDraft\":false,\"assets\":[${APK}]}"
caso "publicada com o arquivo e outros"      nada      nao "{\"isDraft\":false,\"assets\":[{\"name\":\"notas.txt\",\"state\":\"uploaded\"},${APK}]}"
caso "rascunho com o arquivo"                completar nao "{\"isDraft\":true,\"assets\":[${APK}]}"
caso "rascunho sem arquivo"                  completar sim '{"isDraft":true,"assets":[]}'
caso "publicada sem assets"                  completar sim '{"isDraft":false,"assets":[]}'
caso "publicada sem o campo assets"          completar sim '{"isDraft":false}'
caso "publicada com outro arquivo"           completar sim '{"isDraft":false,"assets":[{"name":"outro.apk","state":"uploaded"}]}'
caso "arquivo com caixa diferente"           completar sim '{"isDraft":false,"assets":[{"name":"CoupleSync.apk","state":"uploaded"}]}'
caso "arquivo ainda nao enviado"             completar sim '{"isDraft":false,"assets":[{"name":"couplesync.apk","state":"starter"}]}'
caso "isDraft ausente nao conta como publicada" completar nao "{\"assets\":[${APK}]}"
caso "assets de tipo errado"                 completar sim '{"isDraft":false,"assets":"couplesync.apk"}'
caso "assets com lixo"                       completar sim '{"isDraft":false,"assets":[null,42,"x"]}'

# JSON ilegível: falha, sem decisão.
printf '%s' '{nao e json' > "${TMP}/r.json"
saida="$(bash "${SCRIPT}" "${TMP}/r.json" 2>&1)"; rc=$?
if [ "${rc}" -eq 1 ] && [[ "${saida}" == ::error::* ]]; then echo "OK    JSON ilegivel falha"; else echo "FALHA JSON ilegivel: exit ${rc} '${saida}'"; fail=1; fi
printf '%s' '[]' > "${TMP}/r.json"
bash "${SCRIPT}" "${TMP}/r.json" >/dev/null 2>&1; rc=$?
if [ "${rc}" -eq 1 ]; then echo "OK    JSON que nao e objeto falha"; else echo "FALHA JSON que nao e objeto: exit ${rc}"; fail=1; fi
exit "${fail}"
