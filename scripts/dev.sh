#!/usr/bin/env bash
# Команди для локальної розробки AI_BPMS.
# Використання: scripts/dev.sh <up|down|migrate|test>
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

if [[ -f .env ]]; then
  set -a
  # shellcheck disable=SC1091
  source .env
  set +a
fi

usage() {
  echo "Використання: scripts/dev.sh <up|down|migrate|test>"
  echo "  up       підняти PostgreSQL і дочекатися healthcheck"
  echo "  down     зупинити контейнери (дані у volume зберігаються)"
  echo "  migrate  застосувати EF Core міграції OpenBP.Data"
  echo "  test     dotnet test (Testcontainers сам підніме postgres:16)"
}

cmd_up() {
  docker compose up -d --wait postgres
  docker compose ps postgres
}

cmd_down() {
  docker compose down
}

cmd_migrate() {
  if [[ ! -d src/OpenBP.Data/Migrations ]]; then
    echo "Міграцій ще немає (з'являться у F2-A) — пропускаю."
    return 0
  fi
  dotnet tool restore >/dev/null 2>&1 || true
  dotnet ef database update --project src/OpenBP.Data
}

cmd_test() {
  dotnet test "$@"
}

case "${1:-}" in
  up) cmd_up ;;
  down) cmd_down ;;
  migrate) cmd_migrate ;;
  test) shift; cmd_test "$@" ;;
  *) usage; exit 1 ;;
esac
