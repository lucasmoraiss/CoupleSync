#!/usr/bin/env bash
# Smoke test of the API *container image* (the thing that is deployed), not of the code under `dotnet test`.
#
# Starts the image against a throwaway PostgreSQL container on a private Docker network, waits for the health
# endpoints, then goes through sign-up, group creation and transactions with accented categories, and checks that
# a validation error comes back in Portuguese. Everything it creates is removed at the end.
#
# It exists because the image once could not start at all (no ICU in the Alpine runtime image) while every
# `dotnet test` suite was green. Never point it at a real database: it only ever talks to the one it starts.
#
# Usage: scripts/api-image-smoke-test.sh [image]        (default image: couplesync-api:smoke)
#        Build first:  docker build --file backend/Dockerfile --tag couplesync-api:smoke backend/
#        SMOKE_FORCE_INVARIANT=1 runs the API with globalization-invariant mode forced ON, to prove that the
#        code itself (not only the image) works without culture data.
set -euo pipefail

IMAGE="${1:-couplesync-api:smoke}"
SUFFIX="$$-$RANDOM"
NETWORK="couplesync-smoke-net-$SUFFIX"
DB="couplesync-smoke-db-$SUFFIX"
API="couplesync-smoke-api-$SUFFIX"
DB_PASSWORD="smoke-$RANDOM$RANDOM"
JWT_SECRET="smoke-$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 48)"

cleanup() {
  status=$?
  if [ "$status" -ne 0 ] && docker inspect "$API" >/dev/null 2>&1; then
    echo "--- API container log (last 60 lines) ---"
    docker logs --tail 60 "$API" 2>&1 || true
  fi
  docker rm -f "$API" "$DB" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  echo "cleanup: containers and network removed"
  exit "$status"
}
trap cleanup EXIT

fail() { echo "FAIL: $*"; exit 1; }

# json_field <name>: first string value of "name" in the JSON read from stdin.
json_field() { sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" | head -n 1; }

# request <method> <path> [json-body]: prints "<status> <body>"; uses $TOKEN when set.
request() {
  local method="$1" path="$2" body="${3:-}"
  local args=(--silent --show-error --max-time 30 --request "$method" --write-out '\n%{http_code}')
  [ -n "${TOKEN:-}" ] && args+=(--header "Authorization: Bearer $TOKEN")
  [ -n "$body" ] && args+=(--header 'Content-Type: application/json; charset=utf-8' --data-binary "$body")
  local out
  out="$(curl "${args[@]}" "$BASE$path")"
  echo "${out##*$'\n'} ${out%$'\n'*}"
}

# expect <label> <expected-status> <response> [text that must be in the body]
expect() {
  local label="$1" status="$2" response="$3" needle="${4:-}"
  echo "$label -> ${response:0:330}"
  [ "${response%% *}" = "$status" ] || fail "$label: expected HTTP $status"
  if [ -n "$needle" ]; then
    case "$response" in *"$needle"*) ;; *) fail "$label: body does not contain: $needle" ;; esac
  fi
}

echo "== image: $IMAGE"
docker network create "$NETWORK" >/dev/null

echo "== starting throwaway PostgreSQL ($DB)"
docker run --detach --name "$DB" --network "$NETWORK" \
  --env POSTGRES_USER=postgres --env "POSTGRES_PASSWORD=$DB_PASSWORD" --env POSTGRES_DB=couplesync_smoke \
  postgres:16-alpine >/dev/null
# Ready over TCP (--host): during initialisation PostgreSQL briefly runs a temporary server that only listens
# on the unix socket and is then restarted; checking the socket would report "ready" too early.
db_ready() { docker exec "$DB" pg_isready --host 127.0.0.1 --username postgres --dbname couplesync_smoke >/dev/null 2>&1; }
for _ in $(seq 1 120); do
  db_ready && break
  sleep 1
done
if ! db_ready; then
  echo "--- PostgreSQL container log (last 30 lines) ---"
  docker logs --tail 30 "$DB" 2>&1 || true
  fail "PostgreSQL did not become ready"
fi

EXTRA_ENV=(--env SMOKE_TEST=1)
if [ "${SMOKE_FORCE_INVARIANT:-}" = "1" ]; then
  EXTRA_ENV+=(--env DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true)
  echo "== globalization-invariant mode FORCED ON for this run"
fi

echo "== starting the API container ($API)"
docker run --detach --name "$API" --network "$NETWORK" --publish 127.0.0.1::8080 \
  --env "DATABASE_URL=Host=$DB;Port=5432;Database=couplesync_smoke;Username=postgres;Password=$DB_PASSWORD" \
  --env "JWT__SECRET=$JWT_SECRET" \
  --env ASPNETCORE_ENVIRONMENT=Production \
  "${EXTRA_ENV[@]}" \
  "$IMAGE" >/dev/null
PORT="$(docker port "$API" 8080/tcp | head -n 1 | sed 's/.*://')"
BASE="http://127.0.0.1:$PORT"

echo "== waiting for $BASE/health/live and /health/ready"
live=000; ready=000
for _ in $(seq 1 90); do
  [ "$(docker inspect --format '{{.State.Running}}' "$API")" = "true" ] || fail "the API container exited during start-up"
  live="$(curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' "$BASE/health/live" || true)"
  ready="$(curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' "$BASE/health/ready" || true)"
  [ "$live" = "200" ] && [ "$ready" = "200" ] && break
  sleep 1
done
echo "/health/live -> $live"
echo "/health/ready -> $ready"
[ "$live" = "200" ] && [ "$ready" = "200" ] || fail "health endpoints did not answer 200"

echo "== culture and time-zone data inside the container"
docker exec "$API" sh -c 'echo "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=$DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"; ls /usr/share/zoneinfo/America/Sao_Paulo; ls /usr/lib | grep -c libicu'

# Accented text travels in the request bodies as JSON \u escapes ("Alimentação" is "Alimentação"):
# the server receives the same characters, and the bytes sent do not depend on the code page of this shell.
TOKEN=""
EMAIL="smoke-$SUFFIX@example.com"

response="$(request POST /api/v1/auth/register "{\"email\":\"$EMAIL\",\"name\":\"Teste de Imagem\",\"password\":\"SenhaSegura123\"}")"
expect "register" 201 "$response" '"accessToken"'
TOKEN="$(echo "$response" | json_field accessToken)"

response="$(request POST /api/v1/couples '{}')"
expect "create group" 201 "$response" '"joinCode"'
TOKEN="$(echo "$response" | json_field accessToken)"

response="$(request POST /api/v1/transactions '{"amount":42.5,"currency":"BRL","eventTimestampUtc":"2026-10-01T12:00:00Z","description":"Mercado","category":"Alimenta\u00e7\u00e3o"}')"
expect "transaction with category Alimentação" 201 "$response" '"category":"ALIMENTACAO"'

response="$(request POST /api/v1/transactions '{"amount":120,"currency":"BRL","eventTimestampUtc":"2026-10-02T12:00:00Z","description":"Farm\u00e1cia","category":"Sa\u00fade"}')"
expect "transaction with category Saúde" 201 "$response" '"category":"SAUDE"'

response="$(request GET '/api/v1/transactions?page=1&pageSize=10')"
expect "list transactions" 200 "$response" '"totalCount":2'
case "$response" in *'"category":"ALIMENTACAO"'*'"category":"SAUDE"'*|*'"category":"SAUDE"'*'"category":"ALIMENTACAO"'*) ;; *) fail "the list does not show both categories" ;; esac

response="$(request POST /api/v1/transactions '{"amount":10,"currency":"BRL","eventTimestampUtc":"2026-10-02T12:00:00Z","description":"x","category":"Inexistente"}')"
expect "unknown category" 400 "$response" 'Categorias aceitas: ALIMENTACAO'

response="$(request POST /api/v1/auth/register '{"email":"","name":"","password":""}')"
expect "validation error in Portuguese" 400 "$response" 'VALIDATION_ERROR'
case "$response" in *"must not be empty"*|*"must be"*) fail "validation message is in English" ;; esac
case "$response" in *"E-mail"*) ;; *) fail "validation message does not use the Portuguese field label" ;; esac

# A date-only range on a day whose midnight did not exist in Brasília (daylight saving started): must not be a 500
# now that the image has the real time-zone database.
response="$(request GET '/api/v1/dashboard?startDate=2018-11-04&endDate=2018-11-04')"
expect "dashboard on a daylight-saving gap day" 200 "$response"

echo "== category rules seeded at start-up (stored key, count)"
rules="$(docker exec "$DB" psql --username postgres --dbname couplesync_smoke --tuples-only --no-align \
  --command "select category || ' ' || count(*) from category_rules group by category order by category")"
echo "$rules"
case "$rules" in *ALIMENTACAO*) ;; *) fail "seeded rules lost their category (accent folding did not work in the image)" ;; esac

echo "SMOKE TEST PASSED"
