#!/usr/bin/env bash
# Smoke-перевірка продакшн-розгортання (F4-10, CI): docker-compose.prod.yml піднімається, /health/ready зелений,
# вхід адміністратором, сценарій examples/hello-manual.json виконується з результатом success.
# Використання: scripts/smoke-prod.sh [--no-build]   (потрібен .env — scripts/gen-env.sh)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
set -a
# shellcheck disable=SC1091
source .env
set +a

COMPOSE=(docker compose -f docker-compose.prod.yml)
BASE="http://localhost:${OPENBP_HTTP_PORT:-8080}"
COOKIES="$(mktemp)"
trap 'rm -f "$COOKIES"' EXIT

fail() {
  echo "FAIL: $*" >&2
  "${COMPOSE[@]}" ps >&2 || true
  "${COMPOSE[@]}" logs --tail 80 >&2 || true
  exit 1
}

wait_for() { # опис, команда…
  local what="$1"; shift
  for _ in $(seq 1 150); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  fail "не дочекалися: $what"
}

build=(--build)
[[ "${1:-}" == "--no-build" ]] && build=()
"${COMPOSE[@]}" up -d "${build[@]}"

wait_for "/health/ready через web" curl -fsS "$BASE/health/ready"
echo "ok: /health/ready"
curl -fsS "$BASE/openapi/v1.json" | jq -e '.paths["/api/v1/workflows"]' >/dev/null || fail "/openapi/v1.json через web"
echo "ok: /openapi/v1.json"
curl -fsS -o /dev/null "$BASE/element/openbp-canvas.js" || fail "/element/openbp-canvas.js"
echo "ok: /element/openbp-canvas.js"

curl -fsS -c "$COOKIES" -H 'content-type: application/json' \
  -d "{\"email\":\"${OPENBP_ADMIN_EMAIL}\",\"password\":\"${OPENBP_ADMIN_PASSWORD}\"}" \
  "$BASE/api/v1/auth/login" >/dev/null || fail "вхід адміністратором"
echo "ok: вхід ${OPENBP_ADMIN_EMAIL}"

workflow_id="$(curl -fsS -b "$COOKIES" -H 'content-type: application/json' \
  --data-binary @examples/hello-manual.json "$BASE/api/v1/workflows" | jq -r .id)" || fail "імпорт сценарію"
execution_id="$(curl -fsS -b "$COOKIES" -H 'content-type: application/json' -d '{}' \
  "$BASE/api/v1/workflows/${workflow_id}/executions" | jq -r .id)" || fail "запуск сценарію"
echo "ok: виконання ${execution_id}"

state=""
for _ in $(seq 1 60); do
  state="$(curl -fsS -b "$COOKIES" "$BASE/api/v1/executions/${execution_id}" | jq -r '.summary.state // .state')"
  case "$state" in
    success) break ;;
    error|cancelled|timed_out) fail "виконання завершилось станом ${state}" ;;
  esac
  sleep 2
done
[[ "$state" == "success" ]] || fail "виконання не завершилось (стан ${state})"
echo "ok: виконання success"
