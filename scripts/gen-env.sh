#!/usr/bin/env bash
# Створює .env з .env.example з випадковими секретами: пароль БД, ключ шифрування credentials, пароль адміністратора.
# Використання: scripts/gen-env.sh [файл]   (типово .env; наявний файл не перезаписується)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="${1:-$ROOT/.env}"

if [[ -e "$TARGET" ]]; then
  echo "Файл $TARGET уже є — не перезаписую. Видаліть його або вкажіть інший шлях." >&2
  exit 1
fi

random_password() { openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c "${1:-32}"; }

DB_PASSWORD="$(random_password 32)"
ENCRYPTION_KEY="$(openssl rand -base64 32)"
ADMIN_EMAIL="${OPENBP_ADMIN_EMAIL:-admin@openbp.local}"
ADMIN_PASSWORD="$(random_password 20)"

# Значення без / і & — безпечні для sed; ключ base64 може містити / і +, тому для нього окремий роздільник |.
sed \
  -e "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=${DB_PASSWORD}/" \
  -e "s/Password=openbp\$/Password=${DB_PASSWORD}/" \
  -e "s|^OPENBP_ENCRYPTION_KEY=.*|OPENBP_ENCRYPTION_KEY=${ENCRYPTION_KEY}|" \
  -e "s/^OPENBP_ADMIN_EMAIL=.*/OPENBP_ADMIN_EMAIL=${ADMIN_EMAIL}/" \
  -e "s/^OPENBP_ADMIN_PASSWORD=.*/OPENBP_ADMIN_PASSWORD=${ADMIN_PASSWORD}/" \
  -e "s/^OPENBP_BACKUP_UID=.*/OPENBP_BACKUP_UID=$(id -u)/" \
  -e "s/^OPENBP_BACKUP_GID=.*/OPENBP_BACKUP_GID=$(id -g)/" \
  "$ROOT/.env.example" > "$TARGET"
chmod 600 "$TARGET"

echo "Створено $TARGET (права 600)."
echo "Адміністратор: ${ADMIN_EMAIL} / пароль — OPENBP_ADMIN_PASSWORD у файлі."
echo "Збережіть OPENBP_ENCRYPTION_KEY окремо від бекапів БД: без нього credentials не розшифруються."
