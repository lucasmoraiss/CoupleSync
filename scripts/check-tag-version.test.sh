#!/usr/bin/env bash
# Prova de scripts/check-tag-version.sh sem GitHub: cada caso monta um app.json temporário.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="${HERE}/check-tag-version.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
fail=0

# caso <nome> <esperado: 0|1> <tag> <conteúdo do app.json, ou @ausente para não criar o arquivo> <trecho que a saída deve conter>
caso() {
  local nome="$1" esperado="$2" tag="$3" json="$4" trecho="$5"
  rm -f "${TMP}/app.json"
  [ "${json}" != "@ausente" ] && printf '%s' "${json}" > "${TMP}/app.json"
  local saida rc
  saida="$(bash "${SCRIPT}" "${tag}" "${TMP}/app.json" 2>&1)"; rc=$?
  [ "${rc}" -ne 0 ] && rc=1
  if [ "${rc}" -ne "${esperado}" ]; then
    echo "FALHA ${nome}: esperado ${esperado}, veio ${rc}"; fail=1
  elif [[ "${saida}" != *"${trecho}"* ]]; then
    echo "FALHA ${nome}: a saida nao contem '${trecho}'"; fail=1
  else
    echo "OK    ${nome} (saida ${rc})"
  fi
  echo "      ${saida}"
}

caso "tag igual passa"            0 "v1.2.0"     '{"expo":{"version":"1.2.0"}}' "confere com expo.version 1.2.0"
caso "versao diferente falha"     1 "v1.1.0"     '{"expo":{"version":"1.2.0"}}' "esperado 'v1.2.0'"
caso "sufixo falha"               1 "v1.2.0-pit" '{"expo":{"version":"1.2.0"}}' "esperado 'v1.2.0'"
caso "tag sem v falha"            1 "1.2.0"      '{"expo":{"version":"1.2.0"}}' "esperado 'v1.2.0'"
caso "V maiusculo falha"          1 "V1.2.0"     '{"expo":{"version":"1.2.0"}}' "esperado 'v1.2.0'"
caso "app.json sem versao falha"  1 "v1.2.0"     '{"expo":{"name":"x"}}' "expo.version ausente"
caso "tag vazia falha"            1 ""           '{"expo":{"version":"1.2.0"}}' "esperado 'v1.2.0'"
caso "versao com sufixo no app.json nao casa tag simples" 1 "v1.2.0" '{"expo":{"version":"1.2.0-beta"}}' "esperado 'v1.2.0-beta'"
caso "JSON invalido falha" 1 "v1.2.0" '{nao e json' "Nao foi possivel ler"
caso "app.json ausente falha"     1 "v1.2.0"     '@ausente' "nao encontrado"

# JSON invalido: so a linha ::error::, sem o rastro de pilha do node.
printf '%s' '{nao e json' > "${TMP}/app.json"
saida="$(bash "${SCRIPT}" "v1.2.0" "${TMP}/app.json" 2>&1)"
if [ "$(printf '%s\n' "${saida}" | wc -l)" -eq 1 ] && [[ "${saida}" == ::error::* ]]; then echo "OK    JSON invalido nao despeja rastro de pilha"; else echo "FALHA JSON invalido despejou mais de uma linha"; echo "      ${saida}"; fail=1; fi
exit "${fail}"
