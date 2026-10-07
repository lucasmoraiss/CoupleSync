#!/usr/bin/env bash
# Fumaça de PRODUÇÃO do CoupleSync — somente leitura.
#
# 1. (opcional) espera GET /health mostrar o commit esperado — é assim que se confirma um deploy;
# 2. confere /health/live e /health/ready;
# 3. confere que uma rota protegida sem token devolve 401 no formato único de erro;
# 4. se houver conta de DEMONSTRAÇÃO, entra com ela e faz apenas leituras (GET).
#
# Só usa GET, mais o POST de login da conta de demonstração. Nunca cria, altera ou apaga dado.
# Nunca imprime senha nem token.
#
# Uso: producao-fumaca.sh [commit-esperado]
#   COUPLESYNC_API_URL        base da API (padrão: https://couplesync-api.onrender.com)
#   COUPLESYNC_DEPLOY_WAIT    segundos de espera pelo deploy (padrão: 1200)
#   COUPLESYNC_DEPLOY_POLL    intervalo entre consultas ao /health, em segundos (padrão: 20)
#   COUPLESYNC_DEMO_EMAIL / COUPLESYNC_DEMO_PASSWORD
#                             conta de demonstração; ou o arquivo ~/.couplesync/conta-demo
#                             (ou COUPLESYNC_DEMO_FILE) com essas duas linhas CHAVE=valor.
#                             O e-mail precisa conter "demo": o script recusa qualquer outra conta.
set -euo pipefail

BASE="${COUPLESYNC_API_URL:-https://couplesync-api.onrender.com}"
EXPECTED="${1:-}"
WAIT="${COUPLESYNC_DEPLOY_WAIT:-1200}"
POLL="${COUPLESYNC_DEPLOY_POLL:-20}"
DEMO_FILE="${COUPLESYNC_DEMO_FILE:-$HOME/.couplesync/conta-demo}"
TOKEN=""

fail() { echo "FALHA: $*"; exit 1; }

# json_field <nome>: primeiro valor de texto de "nome" no JSON lido da entrada.
json_field() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -n 1; }

# http_get <caminho>: imprime "<status> <corpo>"; usa $TOKEN quando existe. O plano gratuito do Render
# dorme, então a primeira resposta pode demorar.
http_get() {
  local args=(--silent --show-error --max-time 120 --write-out '\n%{http_code}')
  [ -n "$TOKEN" ] && args+=(--header "Authorization: Bearer $TOKEN")
  local out
  out="$(curl "${args[@]}" "$BASE$1" 2>&1)" || { echo "000 $out"; return 0; }
  echo "${out##*$'\n'} ${out%$'\n'*}"
}

expect_status() {
  local label="$1" status="$2" response="$3"
  echo "$label -> HTTP ${response%% *}"
  [ "${response%% *}" = "$status" ] || fail "$label: esperado HTTP $status. Corpo: ${response#* }"
}

# ── 1. deploy ────────────────────────────────────────────────────────────────
if [ -n "$EXPECTED" ]; then
  short="${EXPECTED:0:7}"
  echo "Esperando /health mostrar o commit $short (até ${WAIT}s)..."
  deadline=$(( $(date +%s) + WAIT ))
  while :; do
    version="$(http_get /health | json_field version)"
    if [ -n "$version" ] && [ "${version:0:7}" = "$short" ]; then
      echo "DEPLOY=confirmado versão=$version"
      break
    fi
    [ "$(date +%s)" -ge "$deadline" ] && fail "deploy não confirmado: /health mostra '${version:-sem resposta}', esperado '$short'"
    sleep "$POLL"
  done
else
  echo "DEPLOY=não conferido (nenhum commit informado); versão atual: $(http_get /health | json_field version)"
fi

# ── 2. saúde ─────────────────────────────────────────────────────────────────
expect_status "GET /health" 200 "$(http_get /health)"
expect_status "GET /health/live" 200 "$(http_get /health/live)"
expect_status "GET /health/ready" 200 "$(http_get /health/ready)"

# ── 3. formato único de erro ─────────────────────────────────────────────────
response="$(http_get /api/v1/auth/me)"
expect_status "GET /api/v1/auth/me sem token" 401 "$response"
case "$response" in
  *'"code"'*'"message"'*) ;;
  *) fail "401 fora do formato {code,message}: ${response#* }" ;;
esac

# ── 4. leituras com a conta de demonstração ──────────────────────────────────
email="${COUPLESYNC_DEMO_EMAIL:-}"
password="${COUPLESYNC_DEMO_PASSWORD:-}"
if { [ -z "$email" ] || [ -z "$password" ]; } && [ -f "$DEMO_FILE" ]; then
  # Lido sem `source`: o arquivo é dado, não código.
  email="$(sed -n 's/^COUPLESYNC_DEMO_EMAIL=//p' "$DEMO_FILE" | head -n 1 | tr -d '\r')"
  password="$(sed -n 's/^COUPLESYNC_DEMO_PASSWORD=//p' "$DEMO_FILE" | head -n 1 | tr -d '\r')"
fi

if [ -z "$email" ] || [ -z "$password" ]; then
  echo "AUTENTICADO=não verificado (sem conta de demonstração configurada)"
  echo "FUMAÇA=ok (parcial)"
  exit 0
fi

case "$email" in
  *demo*) ;;
  *) fail "a conta configurada não é de demonstração (o e-mail precisa conter 'demo'); nada foi enviado" ;;
esac

json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }
body="{\"email\":\"$(json_escape "$email")\",\"password\":\"$(json_escape "$password")\"}"
out="$(printf '%s' "$body" | curl --silent --show-error --max-time 120 --write-out '\n%{http_code}' \
  --request POST --header 'Content-Type: application/json; charset=utf-8' --data-binary @- \
  "$BASE/api/v1/auth/login" 2>&1)" || fail "login da conta de demonstração: sem resposta"
status="${out##*$'\n'}"
echo "POST /api/v1/auth/login (conta de demonstração) -> HTTP $status"
[ "$status" = "200" ] || fail "login da conta de demonstração: HTTP $status"
TOKEN="$(printf '%s' "${out%$'\n'*}" | json_field accessToken)"
[ -n "$TOKEN" ] || fail "login da conta de demonstração: resposta sem accessToken"

expect_status "GET /api/v1/auth/me" 200 "$(http_get /api/v1/auth/me)"
# As duas rotas abaixo exigem que a conta de demonstração pertença a um grupo (403 COUPLE_REQUIRED sem ele).
expect_status "GET /api/v1/dashboard" 200 "$(http_get /api/v1/dashboard)"
expect_status "GET /api/v1/transactions" 200 "$(http_get /api/v1/transactions)"

echo "AUTENTICADO=ok"
echo "FUMAÇA=ok"
