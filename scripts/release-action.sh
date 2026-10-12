#!/usr/bin/env bash
# Uso: release-action.sh [arquivo-json] [--arquivo]
# Decide o que fazer com a Release de uma tag, a partir da saída de `gh release view <tag> --json isDraft,assets`
# (arquivo ausente ou vazio = a Release não existe). Escreve uma palavra:
#   criar      a Release não existe
#   nada       publicada e com couplesync.apk enviado: não se mexe em nada (o link fixo nunca sai do ar)
#   completar  rascunho, ou sem o arquivo enviado: enviar o que falta e publicar
# Com --arquivo escreve, em vez disso, "sim" quando o couplesync.apk precisa ser enviado, "nao" quando já está lá.
# JSON ilegível: exit 1, sem decisão (o workflow para em vez de adivinhar).
set -u
FILE="${1-}"
MODE="${2-}"
if [ "${FILE}" = "--arquivo" ]; then FILE=""; MODE="--arquivo"; fi

if [ -z "${FILE}" ] || [ ! -s "${FILE}" ]; then
  if [ "${MODE}" = "--arquivo" ]; then echo "sim"; else echo "criar"; fi
  exit 0
fi

OUT="$(node -e "
const d = JSON.parse(require('fs').readFileSync(process.argv[1], 'utf8'));
if (d === null || typeof d !== 'object' || Array.isArray(d)) throw new Error('forma inesperada');
const assets = Array.isArray(d.assets) ? d.assets : [];
const ready = assets.some(a => a && a.name === 'couplesync.apk' && a.state === 'uploaded');
process.stdout.write((d.isDraft === false && ready ? 'nada' : 'completar') + ' ' + (ready ? 'nao' : 'sim'));
" "${FILE}" 2>/dev/null)" || {
  echo "::error::Nao foi possivel ler a descricao da Release. Nada foi alterado." >&2
  exit 1
}

if [ "${MODE}" = "--arquivo" ]; then echo "${OUT#* }"; else echo "${OUT% *}"; fi
