#!/usr/bin/env bash
# Uso: check-built-apk-version.sh <tag> <arquivo com a saída de `eas build:view --json`>
# Falha (exit 1) quando o appVersion do build construído não é a tag sem o "v" inicial: o APK que vai para a
# Release tem de se declarar a versão da tag, senão o app instalado compara contra um número que não é o dele.
set -u
TAG="${1-}"
BUILD_JSON="${2-}"

if [ -z "${BUILD_JSON}" ] || [ ! -f "${BUILD_JSON}" ]; then
  echo "::error::Saida do EAS nao encontrada. Nenhuma Release foi criada."
  exit 1
fi

APP_VERSION="$(node -e "
const d = JSON.parse(require('fs').readFileSync(process.argv[1], 'utf8'));
const b = Array.isArray(d) ? d[0] : d;
const v = b && b.appVersion;
process.stdout.write(typeof v === 'string' ? v : '');
" "${BUILD_JSON}" 2>/dev/null)" || {
  echo "::error::Nao foi possivel ler a saida do EAS. Nenhuma Release foi criada."
  exit 1
}

if [ -z "${APP_VERSION}" ]; then
  echo "::error::O build do EAS nao informa appVersion. Nenhuma Release foi criada."
  exit 1
fi

if [ "${TAG}" != "v${APP_VERSION}" ]; then
  echo "::error::O APK construido declara a versao '${APP_VERSION}', mas a tag e '${TAG}' (esperado 'v${APP_VERSION}'). Nenhuma Release foi criada."
  exit 1
fi

echo "O APK construido declara a versao ${APP_VERSION}, igual a tag ${TAG}."
