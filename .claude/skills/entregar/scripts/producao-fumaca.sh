#!/usr/bin/env bash
# Fumaça de PRODUÇÃO do CoupleSync.
#
# 1. (com commit) confirma o deploy por GET /health: a versão publicada é o commit esperado, ou um
#    descendente dele que está em origin/main (outro merge entrou depois), ou um ancestral dele sem
#    nenhuma diferença em backend/ (a API publicada não mudou);
# 2. confere /health/live e /health/ready;
# 3. confere que uma rota protegida sem token devolve 401 no formato único de erro;
# 4. se houver conta de DEMONSTRAÇÃO, entra com ela e faz apenas leituras (GET).
#
# Só usa GET, mais o POST de login da conta de demonstração. Esse login é a ÚNICA escrita em produção:
# cada execução grava uma linha de refresh token (sessão) da conta de demonstração. Fora isso, nada é
# criado, alterado ou apagado. Senha e token nunca são impressos nem passam pela linha de comando.
#
# Uso (a partir da raiz do repositório, depois de buscar origin/main):  producao-fumaca.sh [commit-esperado]
#
# Saída:  0  tudo conferido (ou parcial, sem conta de demonstração)
#         1  FALHA COMPROVADA (com essa linha na saída): a versão esperada está no ar e respondeu errado —
#            erro 500, ou rota pública fora do contrato.
#         3  NÃO CONFIRMADO: prazo esgotado, versão inesperada, sem resposta, indisponibilidade
#            (429/502/503/504: proxy do Render ou banco fora do ar), conta de demonstração recusada ou sem
#            grupo, parâmetro inválido. É uma PARADA para o dono — nunca motivo para reverter sozinho.
#         Qualquer outra saída, ou saída 1 sem a linha "FALHA COMPROVADA:", é "sem resultado".
#
#   COUPLESYNC_API_URL        base da API (padrão: https://couplesync-api.onrender.com)
#   COUPLESYNC_DEPLOY_WAIT    segundos de espera pelo deploy (padrão: 1200)
#   COUPLESYNC_DEPLOY_POLL    intervalo entre consultas ao /health, em segundos (padrão: 20)
#   COUPLESYNC_MAIN_REF       ref local que representa o main remoto (padrão: origin/main)
#   Conta de demonstração — três valores, por variável de ambiente ou no arquivo ~/.couplesync/conta-demo
#   (ou COUPLESYNC_DEMO_FILE), uma linha CHAVE=valor cada:
#     COUPLESYNC_DEMO_EMAIL, COUPLESYNC_DEMO_PASSWORD e
#     COUPLESYNC_DEMO_ALLOW   o e-mail repetido por extenso: a única conta que este script pode usar.
#   O script só entra se EMAIL for idêntico a ALLOW e a parte antes do @ tiver "demo" como palavra
#   inteira (demo@, demo.ana@, ana.demo.123@ — separada por ponto, hífen, sublinhado ou +).
set -euo pipefail

BASE="${COUPLESYNC_API_URL:-https://couplesync-api.onrender.com}"
EXPECTED="${1:-}"
WAIT="${COUPLESYNC_DEPLOY_WAIT:-1200}"
POLL="${COUPLESYNC_DEPLOY_POLL:-20}"
MAIN_REF="${COUPLESYNC_MAIN_REF:-origin/main}"
DEMO_FILE="${COUPLESYNC_DEMO_FILE:-$HOME/.couplesync/conta-demo}"
TOKEN=""

fail() { echo "FALHA COMPROVADA: $*"; exit 1; }
unconfirmed() { echo "NÃO CONFIRMADO: $*"; exit 3; }

case "$WAIT$POLL" in *[!0-9]*|'') unconfirmed "COUPLESYNC_DEPLOY_WAIT e COUPLESYNC_DEPLOY_POLL precisam ser números inteiros" ;; esac
[ "$POLL" -ge 1 ] || unconfirmed "COUPLESYNC_DEPLOY_POLL precisa ser pelo menos 1"

# json_field <nome>: primeiro valor de texto de "nome" no JSON lido da entrada.
json_field() { sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" | head -n 1; }

# http_get <caminho>: imprime "<status> <corpo>" ("000 ..." sem resposta). Com $TOKEN, o cabeçalho vai
# pela entrada padrão do curl, não pela linha de comando. O plano gratuito do Render dorme: a primeira
# resposta pode demorar.
http_get() {
  local out
  if [ -n "$TOKEN" ]; then
    out="$(printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" \
      | curl --config - --silent --show-error --max-time 120 --write-out '\n%{http_code}' "$BASE$1" 2>&1)" \
      || { echo "000 sem resposta"; return 0; }
  else
    out="$(curl --silent --show-error --max-time 120 --write-out '\n%{http_code}' "$BASE$1" 2>&1)" \
      || { echo "000 $out"; return 0; }
  fi
  echo "${out##*$'\n'} ${out%$'\n'*}"
}

# expect_status <rótulo> <status esperado> <resposta> [demo]
# Indisponibilidade (sem resposta, 429, 502, 503, 504) nunca é falha comprovada: pode ser o proxy do Render ou
# o banco fora do ar, sem relação com a entrega. Com "demo", 401/403 também não: é a conta (sem grupo, sessão).
expect_status() {
  local label="$1" status="$2" response="$3" kind="${4:-}" got
  got="${response%% *}"
  echo "$label -> HTTP $got"
  [ "$got" = "$status" ] && return 0
  case "$got" in
    000) unconfirmed "$label: sem resposta da API" ;;
    429|502|503|504) unconfirmed "$label: HTTP $got (indisponibilidade ou limite de requisições)" ;;
    401|403) [ "$kind" = "demo" ] && unconfirmed "$label: HTTP $got — confira a conta de demonstração (ela precisa estar num grupo)" ;;
  esac
  fail "$label: esperado HTTP $status, veio HTTP $got"
}

commit_of() { git rev-parse --verify --quiet "$1^{commit}" 2>/dev/null || true; }

# deploy_state <versão do /health>: confirmado | inalterada | anterior | outra | sem-resposta
deploy_state() {
  local version="$1" published
  [ -n "$version" ] || { echo "sem-resposta"; return; }
  case "$version" in *[!0-9a-f]*) echo "outra"; return ;; esac
  [ "${#version}" -ge 7 ] || { echo "outra"; return; }
  [ "${version:0:7}" = "${EXPECTED_SHA:0:7}" ] && { echo "confirmado"; return; }
  published="$(commit_of "$version")"
  [ -n "$published" ] || { echo "outra"; return; }
  if git merge-base --is-ancestor "$EXPECTED_SHA" "$published" \
     && git merge-base --is-ancestor "$published" "$MAIN_REF"; then
    echo "confirmado"; return
  fi
  if git merge-base --is-ancestor "$published" "$EXPECTED_SHA"; then
    if git diff --quiet "$published" "$EXPECTED_SHA" -- backend/; then echo "inalterada"; else echo "anterior"; fi
    return
  fi
  echo "outra"
}

# ── 1. deploy ────────────────────────────────────────────────────────────────
if [ -n "$EXPECTED" ]; then
  EXPECTED_SHA="$(commit_of "$EXPECTED")"
  [ -n "$EXPECTED_SHA" ] || unconfirmed "o commit '$EXPECTED' não existe neste clone (rode na raiz do repositório, depois do fetch)"
  [ -n "$(commit_of "$MAIN_REF")" ] || unconfirmed "a ref '$MAIN_REF' não existe neste clone"
  echo "Esperando /health mostrar ${EXPECTED_SHA:0:7} ou um descendente em $MAIN_REF (até ${WAIT}s)..."
  deadline=$(( $(date +%s) + WAIT ))
  while :; do
    version="$(http_get /health | json_field version)"
    state="$(deploy_state "$version")"
    case "$state" in
      confirmado) echo "DEPLOY=confirmado versão=$version"; break ;;
      inalterada) echo "DEPLOY=API inalterada versão=$version (nenhuma diferença em backend/ até ${EXPECTED_SHA:0:7})"; break ;;
      outra) unconfirmed "/health mostra '$version', que não é ${EXPECTED_SHA:0:7}, nem descendente dele em $MAIN_REF, nem ancestral" ;;
    esac
    [ "$(date +%s)" -ge "$deadline" ] && unconfirmed "prazo esgotado: /health mostra '${version:-sem resposta}', esperado ${EXPECTED_SHA:0:7}"
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
  *) fail "401 fora do formato {code,message}" ;;
esac

# ── 4. leituras com a conta de demonstração ──────────────────────────────────
email="${COUPLESYNC_DEMO_EMAIL:-}"
password="${COUPLESYNC_DEMO_PASSWORD:-}"
allow="${COUPLESYNC_DEMO_ALLOW:-}"
if [ -z "$email$password$allow" ] && [ -f "$DEMO_FILE" ]; then
  # Lido sem `source`: o arquivo é dado, não código.
  demo_value() { sed -n "s/^$1=//p" "$DEMO_FILE" | head -n 1 | tr -d '\r'; }
  email="$(demo_value COUPLESYNC_DEMO_EMAIL)"
  password="$(demo_value COUPLESYNC_DEMO_PASSWORD)"
  allow="$(demo_value COUPLESYNC_DEMO_ALLOW)"
fi

if [ -z "$email" ] && [ -z "$password" ] && [ -z "$allow" ]; then
  echo "AUTENTICADO=não verificado (sem conta de demonstração configurada)"
  echo "FUMAÇA=ok (parcial)"
  exit 0
fi

if [ -z "$email" ] || [ -z "$password" ] || [ "$email" != "$allow" ]; then
  unconfirmed "conta de demonstração incompleta ou COUPLESYNC_DEMO_ALLOW diferente do e-mail; nada foi enviado"
fi
case "${email%%@*}@" in
  demo@*|demo[._+-]*@*|*[._+-]demo@*|*[._+-]demo[._+-]*@*) ;;
  *) unconfirmed "a conta configurada não é de demonstração (a parte antes do @ precisa ter 'demo' como palavra inteira); nada foi enviado" ;;
esac

json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }
body="{\"email\":\"$(json_escape "$email")\",\"password\":\"$(json_escape "$password")\"}"
out="$(printf '%s' "$body" | curl --silent --show-error --max-time 120 --write-out '\n%{http_code}' \
  --request POST --header 'Content-Type: application/json; charset=utf-8' --data-binary @- \
  "$BASE/api/v1/auth/login" 2>/dev/null)" || unconfirmed "login da conta de demonstração: sem resposta"
status="${out##*$'\n'}"
echo "POST /api/v1/auth/login (conta de demonstração) -> HTTP $status"
case "$status" in
  200) ;;
  500) fail "login da conta de demonstração: HTTP 500" ;;
  *) unconfirmed "a API não aceitou a conta de demonstração (HTTP $status): confira as credenciais ou a disponibilidade" ;;
esac
TOKEN="$(printf '%s' "${out%$'\n'*}" | json_field accessToken)"
[ -n "$TOKEN" ] || fail "login da conta de demonstração: resposta 200 sem accessToken"

expect_status "GET /api/v1/auth/me" 200 "$(http_get /api/v1/auth/me)" demo
# As duas rotas abaixo exigem que a conta de demonstração pertença a um grupo (403 COUPLE_REQUIRED sem ele).
expect_status "GET /api/v1/dashboard" 200 "$(http_get /api/v1/dashboard)" demo
expect_status "GET /api/v1/transactions" 200 "$(http_get /api/v1/transactions)" demo

echo "AUTENTICADO=ok"
echo "FUMAÇA=ok"
