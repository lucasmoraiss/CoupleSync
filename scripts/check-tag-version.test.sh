#!/usr/bin/env bash
# Prova de scripts/check-tag-version.sh sem GitHub: cada caso monta um app.json temporário.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="${HERE}/check-tag-version.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
fail=0

# caso <nome> <esperado: 0|1> <tag> <conteúdo do app.json>
caso() {
  local nome="$1" esperado="$2" tag="$3" json="$4"
  printf '%s' "${json}" > "${TMP}/app.json"
  local saida rc
  saida="$(bash "${SCRIPT}" "${tag}" "${TMP}/app.json" 2>&1)"; rc=$?
  [ "${rc}" -ne 0 ] && rc=1
  if [ "${rc}" -eq "${esperado}" ]; then echo "OK    ${nome} (saida ${rc})"; else echo "FALHA ${nome}: esperado ${esperado}, veio ${rc}"; fail=1; fi
  echo "      ${saida}"
}

caso "tag igual passa"            0 "v1.2.0"     '{"expo":{"version":"1.2.0"}}'
caso "versao diferente falha"     1 "v1.1.0"     '{"expo":{"version":"1.2.0"}}'
caso "sufixo falha"               1 "v1.2.0-pit" '{"expo":{"version":"1.2.0"}}'
caso "tag sem v falha"            1 "1.2.0"      '{"expo":{"version":"1.2.0"}}'
caso "app.json sem versao falha"  1 "v1.2.0"     '{"expo":{"name":"x"}}'
caso "tag vazia falha"            1 ""           '{"expo":{"version":"1.2.0"}}'
caso "versao com sufixo no app.json nao casa tag simples" 1 "v1.2.0" '{"expo":{"version":"1.2.0-beta"}}'
exit "${fail}"
