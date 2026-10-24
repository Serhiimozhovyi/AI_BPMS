#!/usr/bin/env bash
# Разовий бекап БД продакшн-розгортання (F4-11): той самий openbp-backup, що й у розкладі сервісу backup.
# Використання: scripts/backup.sh   → друкує шлях до файлу на хості (у OPENBP_BACKUP_DIR, типово ./backups).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
COMPOSE=(docker compose -f docker-compose.prod.yml)
BACKUP_DIR="$(grep -E '^OPENBP_BACKUP_DIR=' .env 2>/dev/null | cut -d= -f2- || true)"
BACKUP_DIR="${BACKUP_DIR:-./backups}"

if [[ -n "$("${COMPOSE[@]}" ps --status running -q backup)" ]]; then
  out="$("${COMPOSE[@]}" exec -T backup openbp-backup)"
else
  out="$("${COMPOSE[@]}" run --rm -T --entrypoint openbp-backup backup)"
fi
echo "$out" >&2

file="$(sed -n 's/.* ok \(\/backups\/[^ ]*\.dump\) .*/\1/p' <<<"$out" | tail -1)"
[[ -n "$file" ]] || { echo "Бекап не створено." >&2; exit 1; }
echo "${BACKUP_DIR%/}/$(basename "$file")"
