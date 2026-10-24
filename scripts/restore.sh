#!/usr/bin/env bash
# Відновлення БД продакшн-розгортання з бекапу (F4-11): зупиняє api, worker і backup, пересоздає базу,
# pg_restore, застосовує міграції (migrate) і піднімає сервіси назад. Поточні дані бази буде ЗАМІНЕНО.
# Використання: scripts/restore.sh [--yes] <файл.dump>
# Credentials розшифруються лише з тим самим OPENBP_ENCRYPTION_KEY, що був при створенні бекапу.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
yes=0
if [[ "${1:-}" == "--yes" ]]; then yes=1; shift; fi
FILE="${1:-}"
[[ -n "$FILE" && -r "$FILE" ]] || { echo "Використання: scripts/restore.sh [--yes] <файл.dump> (файл не знайдено або не читається)" >&2; exit 2; }
FILE="$(cd "$(dirname "$FILE")" && pwd)/$(basename "$FILE")"

cd "$ROOT"
set -a
# shellcheck disable=SC1091
source .env
set +a
DB="${POSTGRES_DB:-openbp}"
USER_NAME="${POSTGRES_USER:-openbp}"
COMPOSE=(docker compose -f docker-compose.prod.yml)

if [[ $yes -ne 1 ]]; then
  read -r -p "База '$DB' буде замінена вмістом $FILE. Продовжити? [y/N] " answer
  [[ "$answer" =~ ^[YyТт]$ ]] || { echo "Скасовано."; exit 1; }
fi

echo "1/5 Зупиняю api, worker, backup…"
"${COMPOSE[@]}" stop api worker backup

echo "2/5 Пересоздаю базу $DB…"
"${COMPOSE[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "$USER_NAME" -d postgres \
  -c "DROP DATABASE IF EXISTS \"$DB\" WITH (FORCE);" -c "CREATE DATABASE \"$DB\" OWNER \"$USER_NAME\";"

echo "3/5 pg_restore…"
"${COMPOSE[@]}" exec -T postgres pg_restore -U "$USER_NAME" -d "$DB" --no-owner --exit-on-error < "$FILE"

echo "4/5 Міграції…"
"${COMPOSE[@]}" run --rm --no-deps migrate

echo "5/5 Запускаю сервіси…"
"${COMPOSE[@]}" up -d --wait api worker web backup
echo "Відновлено з $FILE."
