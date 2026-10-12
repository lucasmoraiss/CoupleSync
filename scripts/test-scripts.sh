#!/usr/bin/env bash
# Roda os testes dos scripts de publicação (sem rede, segundos). Usado pelo passo do job "Build & Test".
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
fail=0
for t in check-tag-version.test.sh check-built-apk-version.test.sh release-action.test.sh; do
  echo "== ${t}"
  bash "${HERE}/${t}" || fail=1
done
exit "${fail}"
