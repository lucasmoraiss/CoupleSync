#!/usr/bin/env bash
# The API that the "App E2E" workflow tests the app against: the real container image plus a throwaway
# PostgreSQL, on a private Docker network, with the API published on 127.0.0.1 of this machine (the Android
# emulator reaches it as http://10.0.2.2:<port>). Same recipe as scripts/api-image-smoke-test.sh.
# It only ever talks to the database it starts; never point it at a real one.
#
# Usage: scripts/app-e2e-api.sh start [image]     (default image: couplesync-api:e2e; build it first with
#                                                  docker build --file backend/Dockerfile --tag couplesync-api:e2e backend/)
#        scripts/app-e2e-api.sh logs <directory>  writes api.log and postgres.log there
#        scripts/app-e2e-api.sh stop              removes the containers and the network
#        E2E_API_PORT changes the published port (default 5000).
set -euo pipefail

COMMAND="${1:-}"
NETWORK="couplesync-e2e-net"
DB="couplesync-e2e-db"
API="couplesync-e2e-api"
PORT="${E2E_API_PORT:-5000}"
BASE="http://127.0.0.1:$PORT"

fail() { echo "FAIL: $*" >&2; exit 1; }

remove_all() {
  docker rm -f "$API" "$DB" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
}

start() {
  local image="${1:-couplesync-api:e2e}"
  local db_password jwt_secret live ready
  db_password="e2e-$RANDOM$RANDOM"
  # 48 random characters, generated here and never printed: the API refuses to start without a real secret.
  jwt_secret="$(head -c 96 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 48)"
  [ "${#jwt_secret}" = "48" ] || fail "could not generate the JWT secret"

  remove_all
  docker network create "$NETWORK" >/dev/null

  echo "== starting throwaway PostgreSQL ($DB)"
  docker run --detach --name "$DB" --network "$NETWORK" \
    --env POSTGRES_USER=postgres --env "POSTGRES_PASSWORD=$db_password" --env POSTGRES_DB=couplesync_e2e \
    postgres:16-alpine >/dev/null
  # Ready over TCP (--host): during initialisation PostgreSQL briefly runs a temporary server that only listens
  # on the unix socket and is then restarted; checking the socket would report "ready" too early.
  db_ready() { docker exec "$DB" pg_isready --host 127.0.0.1 --username postgres --dbname couplesync_e2e >/dev/null 2>&1; }
  for _ in $(seq 1 120); do
    db_ready && break
    sleep 1
  done
  if ! db_ready; then
    docker logs --tail 30 "$DB" 2>&1 || true
    fail "PostgreSQL did not become ready"
  fi

  # AI: the fake provider is the only link of every chain here (no key, no call to anyone). The API itself refuses
  # to start with it where a real provider key or the RENDER variable exists.
  # The whole emulator reaches the API from one address, and every flow signs up, signs in and signs out:
  # the production limits (5 per minute per address) would reject the tests, so they are raised here only.
  # App update: no lookup of the latest release on GitHub (empty address). What the flows see must not depend on
  # which version is published at the time, so the "new version" notice never shows in them.
  echo "== starting the API container ($API) on $BASE"
  docker run --detach --name "$API" --network "$NETWORK" --publish "127.0.0.1:$PORT:8080" \
    --env "DATABASE_URL=Host=$DB;Port=5432;Database=couplesync_e2e;Username=postgres;Password=$db_password" \
    --env "JWT__SECRET=$jwt_secret" \
    --env ASPNETCORE_ENVIRONMENT=Production \
    --env RateLimiting__Auth__PermitLimit=1000 \
    --env RateLimiting__CoupleJoin__PermitLimit=1000 \
    --env Ai__UseFakeProvider=true \
    --env AppUpdate__LatestReleaseUrl= \
    "$image" >/dev/null

  echo "== waiting for $BASE/health/live and /health/ready"
  live=000; ready=000
  for _ in $(seq 1 120); do
    if [ "$(docker inspect --format '{{.State.Running}}' "$API")" != "true" ]; then
      docker logs --tail 60 "$API" 2>&1 || true
      fail "the API container exited during start-up"
    fi
    live="$(curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' "$BASE/health/live" || true)"
    ready="$(curl --silent --output /dev/null --max-time 5 --write-out '%{http_code}' "$BASE/health/ready" || true)"
    [ "$live" = "200" ] && [ "$ready" = "200" ] && break
    sleep 1
  done
  echo "/health/live -> $live"
  echo "/health/ready -> $ready"
  if [ "$live" != "200" ] || [ "$ready" != "200" ]; then
    docker logs --tail 60 "$API" 2>&1 || true
    fail "health endpoints did not answer 200"
  fi
  echo "TEST API READY: $BASE"
}

logs() {
  local directory="${1:-}"
  [ -n "$directory" ] || fail "usage: $0 logs <directory>"
  mkdir -p "$directory"
  docker logs "$API" > "$directory/api.log" 2>&1 || echo "no API container" > "$directory/api.log"
  docker logs "$DB" > "$directory/postgres.log" 2>&1 || echo "no PostgreSQL container" > "$directory/postgres.log"
  echo "container logs written to $directory"
}

case "$COMMAND" in
  start) start "${2:-}" ;;
  logs) logs "${2:-}" ;;
  stop) remove_all; echo "containers and network removed" ;;
  *) fail "usage: $0 start [image] | logs <directory> | stop" ;;
esac
