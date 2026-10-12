#!/usr/bin/env bash
# Prova de scripts/check-built-apk-version.sh sem EAS: cada caso monta a saída de `eas build:view --json`.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
SCRIPT="${HERE}/check-built-apk-version.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
fail=0

# caso <nome> <esperado: 0|1> <tag> <json, ou @ausente> <trecho que a saída deve conter>
caso() {
  local nome="$1" esperado="$2" tag="$3" json="$4" trecho="$5"
  rm -f "${TMP}/b.json"
  [ "${json}" != "@ausente" ] && printf '%s' "${json}" > "${TMP}/b.json"
  local saida rc
  saida="$(bash "${SCRIPT}" "${tag}" "${TMP}/b.json" 2>&1)"; rc=$?
  [ "${rc}" -ne 0 ] && rc=1
  if [ "${rc}" -ne "${esperado}" ]; then
    echo "FALHA ${nome}: esperado ${esperado}, veio ${rc}"; fail=1
  elif [[ "${saida}" != *"${trecho}"* ]]; then
    echo "FALHA ${nome}: a saida nao contem '${trecho}' ('${saida}')"; fail=1
  else
    echo "OK    ${nome} (saida ${rc})"
  fi
}

caso "appVersion igual a tag passa"      0 "v1.2.0"     '{"appVersion":"1.2.0","id":"x"}' "declara a versao 1.2.0"
caso "lista de um build passa"           0 "v1.2.0"     '[{"appVersion":"1.2.0"}]' "declara a versao 1.2.0"
caso "appVersion antiga falha"           1 "v1.3.0"     '{"appVersion":"1.2.0"}' "declara a versao '1.2.0'"
caso "sufixo na tag falha"               1 "v1.2.0-pit" '{"appVersion":"1.2.0"}' "esperado 'v1.2.0'"
caso "tag sem v falha"                   1 "1.2.0"      '{"appVersion":"1.2.0"}' "esperado 'v1.2.0'"
caso "tag vazia falha"                   1 ""           '{"appVersion":"1.2.0"}' "esperado 'v1.2.0'"
caso "sem appVersion falha"              1 "v1.2.0"     '{"id":"x"}' "nao informa appVersion"
caso "appVersion nao texto falha"        1 "v1.2.0"     '{"appVersion":120}' "nao informa appVersion"
caso "lista vazia falha"                 1 "v1.2.0"     '[]' "nao informa appVersion"
caso "JSON invalido falha"               1 "v1.2.0"     '{nao e json' "Nao foi possivel ler"
caso "arquivo ausente falha"             1 "v1.2.0"     '@ausente' "nao encontrada"
exit "${fail}"
