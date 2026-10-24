#!/usr/bin/env bash
# Smoke бекапу і відновлення (F4-11, CI) поверх запущеного docker-compose.prod.yml (після scripts/smoke-prod.sh):
# сценарій з webhook-тригером і credential (токен) → scripts/backup.sh → обидва видалено → scripts/restore.sh →
# сценарій є, а webhook приймає той самий токен (credential розшифровується) і відхиляє чужий.
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
TOKEN="$(openssl rand -hex 16)"
HOOK="smoke-backup-$(openssl rand -hex 4)"

fail() {
  echo "FAIL: $*" >&2
  "${COMPOSE[@]}" ps >&2 || true
  "${COMPOSE[@]}" logs --tail 60 api backup >&2 || true
  exit 1
}
api() { curl -fsS -b "$COOKIES" -H 'content-type: application/json' "$@"; }
login() {
  curl -fsS -c "$COOKIES" -H 'content-type: application/json' \
    -d "{\"email\":\"${OPENBP_ADMIN_EMAIL}\",\"password\":\"${OPENBP_ADMIN_PASSWORD}\"}" \
    "$BASE/api/v1/auth/login" >/dev/null || fail "вхід адміністратором"
}
hook_status() { curl -s -o /dev/null -w '%{http_code}' -X POST -H "X-Webhook-Token: $1" -d '{}' "$BASE/webhook/$HOOK"; }

login
credential_id="$(api -d "{\"name\":\"smoke-backup\",\"type\":\"webhook.header\",\"data\":{\"token\":\"$TOKEN\"}}" \
  "$BASE/api/v1/credentials" | jq -r .id)" || fail "створення credential"
workflow_id="$(api -d @- "$BASE/api/v1/workflows" <<JSON | jq -r .id
{
  "formatVersion": 1,
  "name": "Smoke: бекап",
  "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
  "nodes": [
    { "id": "n_hook", "name": "Webhook", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
      "parameters": { "method": "POST", "path": "$HOOK", "auth": { "type": "header", "credentialId": "$credential_id" } } }
  ],
  "connections": [],
  "pinData": {}
}
JSON
)" || fail "створення сценарію"
api -X POST "$BASE/api/v1/workflows/$workflow_id/activate" >/dev/null || fail "активація сценарію"
[[ "$(hook_status "$TOKEN")" == "202" ]] || fail "webhook до бекапу не прийняв токен"
echo "ok: сценарій $workflow_id і credential $credential_id"

file="$(scripts/backup.sh)" || fail "scripts/backup.sh"
[[ -s "$file" ]] || fail "файл бекапу $file порожній або не читається"
echo "ok: бекап $file"

api -X POST "$BASE/api/v1/workflows/$workflow_id/deactivate" >/dev/null || fail "деактивація сценарію"
api -X DELETE "$BASE/api/v1/workflows/$workflow_id" >/dev/null || fail "видалення сценарію"
api -X DELETE "$BASE/api/v1/credentials/$credential_id" >/dev/null || fail "видалення credential"
[[ "$(curl -s -o /dev/null -w '%{http_code}' -b "$COOKIES" "$BASE/api/v1/workflows/$workflow_id")" == "404" ]] \
  || fail "сценарій не видалено"
echo "ok: видалено"

scripts/restore.sh --yes "$file" || fail "scripts/restore.sh"

for _ in $(seq 1 60); do curl -fsS "$BASE/health/ready" >/dev/null 2>&1 && break; sleep 2; done
login
api "$BASE/api/v1/workflows/$workflow_id" >/dev/null || fail "сценарій не відновлено"
api "$BASE/api/v1/credentials/$credential_id" >/dev/null || fail "credential не відновлено"
[[ "$(hook_status wrong-token)" == "401" ]] || fail "webhook після відновлення прийняв чужий токен"
[[ "$(hook_status "$TOKEN")" == "202" ]] || fail "webhook після відновлення не прийняв токен (credential не розшифровується?)"
echo "ok: відновлено, credential розшифровується"
