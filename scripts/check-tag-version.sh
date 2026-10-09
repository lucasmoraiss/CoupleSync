#!/usr/bin/env bash
# Uso: check-tag-version.sh <tag> [caminho/do/app.json]
# Falha (exit 1) quando a tag não é exatamente "v" + expo.version do app.json.
# O app compara o versionName instalado com a tag da Release mais recente: tag e versão têm de andar juntas.
set -u
TAG="${1-}"
APP_JSON="${2-mobile/app.json}"

if [ ! -f "${APP_JSON}" ]; then
  echo "::error::${APP_JSON} nao encontrado. Nenhum build foi gasto."
  exit 1
fi

VERSION="$(node -p "const v=((JSON.parse(require('fs').readFileSync(process.argv[1],'utf8')).expo)||{}).version; typeof v==='string'?v:''" "${APP_JSON}" 2>/dev/null)" || {
  echo "::error::Nao foi possivel ler ${APP_JSON}. Nenhum build foi gasto."
  exit 1
}

if [ -z "${VERSION}" ]; then
  echo "::error::expo.version ausente em ${APP_JSON}. Nenhum build foi gasto."
  exit 1
fi

if [ "${TAG}" != "v${VERSION}" ]; then
  echo "::error::A tag '${TAG}' nao e v<expo.version> (esperado 'v${VERSION}', de ${APP_JSON}). Nenhum build foi gasto."
  exit 1
fi

echo "Tag ${TAG} confere com expo.version ${VERSION}."
