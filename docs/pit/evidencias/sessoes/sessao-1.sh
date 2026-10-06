#!/usr/bin/env bash
# =============================================================================
# CoupleSync - Sessao de Teste Exploratorio #1
# Escopo: /api/v1/auth/*  e  /api/v1/couples/*
# Caixa-preta contra API local em http://localhost:5000
# Reexecutavel do zero. E-mails unicos por execucao (prefixo s1- + timestamp).
# Requisitos: bash, curl, python3 (para extrair campos JSON).
# =============================================================================
set -u

BASE="http://localhost:5000"
API="$BASE/api/v1"
TS="$(date +%Y%m%d%H%M%S)"
PWD_OK="Senha@Forte123"

# python helper: extrai um campo de um JSON vindo do stdin (dot path simples)
jget() { python3 -c "import sys,json
try:
    d=json.load(sys.stdin)
except Exception:
    print(''); sys.exit(0)
path='''$1'''.split('.')
for p in path:
    if isinstance(d,list):
        try: d=d[int(p)]
        except Exception: d=''; break
    elif isinstance(d,dict):
        d=d.get(p,'')
    else:
        d=''; break
print(d if d is not None else '')"; }

# Executa uma requisicao, imprime cabecalho do caso, comando, status, tempo e corpo.
# Uso: req "1.NN" "descricao" curl-args...
# Guarda o corpo em RESP e o status em STATUS.
req() {
  local caso="$1"; shift
  local desc="$1"; shift
  echo ""
  echo "### CASO ${caso} — ${desc}"
  echo "COMANDO: curl -s $*"
  local tmp; tmp="$(mktemp)"
  # -w escreve status e tempo no fim; separamos do corpo por marcador
  local out
  out="$(curl -s -w $'\n__HTTP__ %{http_code} __TIME__ %{time_total}s' "$@" 2>&1)"
  STATUS="$(printf '%s' "$out" | sed -n 's/.*__HTTP__ \([0-9][0-9]*\) __TIME__.*/\1/p' | tail -1)"
  local ttime; ttime="$(printf '%s' "$out" | sed -n 's/.*__TIME__ \(.*\)/\1/p' | tail -1)"
  RESP="$(printf '%s' "$out" | sed 's/\n*__HTTP__.*//; ')"
  # remove a ultima linha de marcador do corpo
  RESP="$(printf '%s' "$out" | sed '$ s/__HTTP__.*//')"
  rm -f "$tmp"
  echo "STATUS HTTP: ${STATUS}"
  echo "TEMPO: ${ttime}"
  echo "CORPO:"
  printf '%s\n' "$RESP"
}

echo "============================================================"
echo "SESSAO 1 - CoupleSync - execucao TS=${TS}"
echo "Data/hora: $(date -u +%Y-%m-%dT%H:%M:%SZ) (UTC)"
echo "Base: ${BASE}"
echo "============================================================"

# -----------------------------------------------------------------------------
# CAMINHO FELIZ
# -----------------------------------------------------------------------------
EMAIL_A="s1-a-${TS}@test.local"
EMAIL_B="s1-b-${TS}@test.local"
EMAIL_C="s1-c-${TS}@test.local"
EMAIL_D="s1-d-${TS}@test.local"   # usuario sem casal (teste transactions)

req "1.01" "Cadastrar usuario A" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_A\",\"name\":\"Alice S1\",\"password\":\"$PWD_OK\"}"
TOKEN_A="$(printf '%s' "$RESP" | jget accessToken)"
REFRESH_A="$(printf '%s' "$RESP" | jget refreshToken)"

req "1.02" "Cadastrar usuario B" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_B\",\"name\":\"Bob S1\",\"password\":\"$PWD_OK\"}"
TOKEN_B="$(printf '%s' "$RESP" | jget accessToken)"

req "1.03" "Login do usuario A" -X POST "$API/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_A\",\"password\":\"$PWD_OK\"}"
TOKEN_A="$(printf '%s' "$RESP" | jget accessToken)"
REFRESH_A="$(printf '%s' "$RESP" | jget refreshToken)"

req "1.04" "Refresh do token de A (rotaciona refresh token)" -X POST "$API/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"$REFRESH_A\"}"
REFRESH_A2="$(printf '%s' "$RESP" | jget refreshToken)"
TOKEN_A="$(printf '%s' "$RESP" | jget accessToken)"

req "1.05" "A cria o casal" -X POST "$API/couples" \
  -H "Authorization: Bearer $TOKEN_A"
JOINCODE="$(printf '%s' "$RESP" | jget joinCode)"
TOKEN_A="$(printf '%s' "$RESP" | jget accessToken)"   # token agora com couple_id
echo "    [JOINCODE capturado: ${JOINCODE}]"

req "1.06" "B entra no casal usando o codigo" -X POST "$API/couples/join" \
  -H "Authorization: Bearer $TOKEN_B" -H "Content-Type: application/json" \
  -d "{\"joinCode\":\"$JOINCODE\"}"
TOKEN_B="$(printf '%s' "$RESP" | jget accessToken)"   # token agora com couple_id

req "1.07" "GET /couples/me do usuario A" -X GET "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_A"

req "1.08" "GET /couples/me do usuario B" -X GET "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_B"

# -----------------------------------------------------------------------------
# ENTRADAS INVALIDAS E LIMITES (register)
# -----------------------------------------------------------------------------
req "1.09" "Register com e-mail duplicado (mesmo do A)" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_A\",\"name\":\"Dup\",\"password\":\"$PWD_OK\"}"

req "1.10" "Register com e-mail malformado" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"nao-e-email\",\"name\":\"Mal\",\"password\":\"$PWD_OK\"}"

req "1.11" "Register com senha de 7 caracteres" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"s1-p7-${TS}@test.local\",\"name\":\"P7\",\"password\":\"1234567\"}"

req "1.12" "Register com senha '12345678' (8 chars, fraca)" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"s1-weak-${TS}@test.local\",\"name\":\"Weak\",\"password\":\"12345678\"}"

P200="$(python3 -c "print('a'*200)")"
req "1.13" "Register com senha de 200 caracteres" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"s1-p200-${TS}@test.local\",\"name\":\"P200\",\"password\":\"$P200\"}"

req "1.14" "Register sem o campo password" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"s1-nopass-${TS}@test.local\",\"name\":\"NoPass\"}"

req "1.15" "Register sem o campo email" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"name\":\"NoEmail\",\"password\":\"$PWD_OK\"}"

req "1.16" "Register com JSON malformado" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"s1-bad-${TS}@test.local\",\"name\":\"Bad\",\"password\":"

req "1.17" "Register com corpo vazio" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d ""

req "1.18" "Login sem o campo password" -X POST "$API/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_A\"}"

# -----------------------------------------------------------------------------
# AUTENTICACAO
# -----------------------------------------------------------------------------
echo ""
echo "### CASO 1.19 — Login com senha errada 10 vezes seguidas (status/tempo por tentativa)"
for i in $(seq 1 10); do
  resp="$(curl -s -w '\n__HTTP__ %{http_code} __TIME__ %{time_total}s' -X POST "$API/auth/login" \
    -H "Content-Type: application/json" \
    -d "{\"email\":\"$EMAIL_A\",\"password\":\"senhaErrada!$i\"}")"
  st="$(printf '%s' "$resp" | sed -n 's/.*__HTTP__ \([0-9]*\) __TIME__.*/\1/p')"
  tm="$(printf '%s' "$resp" | sed -n 's/.*__TIME__ \(.*\)/\1/p')"
  bd="$(printf '%s' "$resp" | sed '$ s/__HTTP__.*//')"
  echo "  tentativa $i: STATUS=$st TEMPO=$tm CORPO=$(printf '%s' "$bd" | tr -d '\n')"
done

req "1.20" "Login com e-mail inexistente (comparar msg/tempo com senha errada)" -X POST "$API/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"s1-naoexiste-${TS}@test.local\",\"password\":\"qualquer123\"}"

req "1.21" "Refresh com token invalido (string aleatoria)" -X POST "$API/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"token-invalido-aleatorio-123\"}"

req "1.22" "Refresh reutilizando token JA rotacionado (REFRESH_A do caso 1.04 antes do refresh)" -X POST "$API/auth/refresh" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"$REFRESH_A\"}"

req "1.23" "Chamada autenticada SEM token (GET /couples/me)" -X GET "$API/couples/me"

# Token adulterado: altera o ultimo caractere da assinatura (3o segmento)
TOKEN_TAMPERED="$(python3 -c "
t='''$TOKEN_A'''
parts=t.split('.')
if len(parts)==3:
    sig=parts[2]
    last=sig[-1]
    repl='A' if last!='A' else 'B'
    parts[2]=sig[:-1]+repl
print('.'.join(parts))")"
req "1.24" "Chamada autenticada com token ADULTERADO (assinatura invalida)" -X GET "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_TAMPERED"

req "1.25" "Chamada autenticada com token de OUTRO esquema (Basic)" -X GET "$API/couples/me" \
  -H "Authorization: Basic $(printf 'user:pass' | base64)"

# -----------------------------------------------------------------------------
# CASAL
# -----------------------------------------------------------------------------
# Cadastrar C e D para os proximos testes
req "1.27" "Cadastrar usuario C (para testes de casal)" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_C\",\"name\":\"Carol S1\",\"password\":\"$PWD_OK\"}"
TOKEN_C="$(printf '%s' "$RESP" | jget accessToken)"

req "1.28" "Cadastrar usuario D (sem casal, para teste de transactions)" -X POST "$API/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"email\":\"$EMAIL_D\",\"name\":\"Dan S1\",\"password\":\"$PWD_OK\"}"
TOKEN_D="$(printf '%s' "$RESP" | jget accessToken)"

req "1.29" "C: join com codigo inexistente ZZZ999" -X POST "$API/couples/join" \
  -H "Authorization: Bearer $TOKEN_C" -H "Content-Type: application/json" \
  -d "{\"joinCode\":\"ZZZ999\"}"

JOINCODE_LOWER="$(printf '%s' "$JOINCODE" | tr 'A-Z' 'a-z')"
req "1.30" "C: join com o codigo do casal em MINUSCULAS ($JOINCODE_LOWER)" -X POST "$API/couples/join" \
  -H "Authorization: Bearer $TOKEN_C" -H "Content-Type: application/json" \
  -d "{\"joinCode\":\"$JOINCODE_LOWER\"}"
TOKEN_C="$(printf '%s' "$RESP" | jget accessToken)"

req "1.31" "GET /couples/me de A APOS entrada de C (terceiro membro?)" -X GET "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_A"

req "1.32" "GET /couples/me de B APOS entrada de C" -X GET "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_B"

req "1.33" "GET /couples/me de C (ve o mesmo casal?)" -X GET "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_C"

req "1.34" "A (ja tem casal) tenta CRIAR outro casal" -X POST "$API/couples" \
  -H "Authorization: Bearer $TOKEN_A"

req "1.35" "A (ja tem casal) tenta ENTRAR em outro casal (codigo qualquer)" -X POST "$API/couples/join" \
  -H "Authorization: Bearer $TOKEN_A" -H "Content-Type: application/json" \
  -d "{\"joinCode\":\"ABC123\"}"

req "1.36" "D (sem casal) chama GET /api/v1/transactions (espera 403 COUPLE_REQUIRED)" -X GET "$API/transactions" \
  -H "Authorization: Bearer $TOKEN_D"

# 20 tentativas de join com codigos aleatorios (ha bloqueio/rate limit?)
echo ""
echo "### CASO 1.37 — 20 tentativas seguidas de join com codigos aleatorios (ha bloqueio?)"
for i in $(seq 1 20); do
  code="$(python3 -c "import random,string;print(''.join(random.choices(string.ascii_uppercase+string.digits,k=6)))")"
  resp="$(curl -s -w '\n__HTTP__ %{http_code} __TIME__ %{time_total}s' -X POST "$API/couples/join" \
    -H "Authorization: Bearer $TOKEN_D" -H "Content-Type: application/json" \
    -d "{\"joinCode\":\"$code\"}")"
  st="$(printf '%s' "$resp" | sed -n 's/.*__HTTP__ \([0-9]*\) __TIME__.*/\1/p')"
  tm="$(printf '%s' "$resp" | sed -n 's/.*__TIME__ \(.*\)/\1/p')"
  bd="$(printf '%s' "$resp" | sed '$ s/__HTTP__.*//')"
  echo "  tentativa $i (code=$code): STATUS=$st TEMPO=$tm CORPO=$(printf '%s' "$bd" | tr -d '\n')"
done

req "1.38" "Join com codigo de tamanho invalido (3 chars) - validacao de formato" -X POST "$API/couples/join" \
  -H "Authorization: Bearer $TOKEN_D" -H "Content-Type: application/json" \
  -d "{\"joinCode\":\"ABC\"}"

req "1.39" "Join com codigo vazio" -X POST "$API/couples/join" \
  -H "Authorization: Bearer $TOKEN_D" -H "Content-Type: application/json" \
  -d "{\"joinCode\":\"\"}"

req "1.40" "Criar casal SEM token (auth obrigatoria)" -X POST "$API/couples"

# Procurar endpoints de sair/remover/trocar codigo (descoberta)
req "1.41" "DESCOBERTA: DELETE /couples/me (sair do casal existe?)" -X DELETE "$API/couples/me" \
  -H "Authorization: Bearer $TOKEN_C"

req "1.42" "DESCOBERTA: POST /couples/leave (sair do casal existe?)" -X POST "$API/couples/leave" \
  -H "Authorization: Bearer $TOKEN_C"

req "1.43" "DESCOBERTA: POST /couples/code/rotate (trocar codigo existe?)" -X POST "$API/couples/code/rotate" \
  -H "Authorization: Bearer $TOKEN_A"

echo ""
echo "============================================================"
echo "FIM DA SESSAO 1 - TS=${TS}"
echo "============================================================"
