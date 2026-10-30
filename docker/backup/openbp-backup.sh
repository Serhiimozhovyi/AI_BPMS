#!/bin/sh
# Разовий бекап БД AI_BPMS (F4-11): pg_dump -Fc у $OPENBP_BACKUP_DIR, потім ротація старших за
# $OPENBP_BACKUP_KEEP_DAYS днів. Підключення — стандартні PGHOST/PGPORT/PGUSER/PGPASSWORD/PGDATABASE.
# Результат — один рядок у журналі контейнера і рядок у таблиці backup_runs (F6-07: Api сповіщає адміністратора про
# збій бекапу); код виходу ≠ 0 при помилці.
set -eu

# Запис результату в backup_runs; БД може бути недоступна чи без міграцій — тоді лише журнал.
record() {
  printf "INSERT INTO backup_runs (ok, message) VALUES (%s, :'msg');\n" "$1" \
    | psql --no-psqlrc --quiet --set ON_ERROR_STOP=1 -v msg="$2" >/dev/null 2>&1 || true
}

DIR="${OPENBP_BACKUP_DIR:-/backups}"
KEEP_DAYS="${OPENBP_BACKUP_KEEP_DAYS:-14}"
OWNER="${OPENBP_BACKUP_UID:-0}:${OPENBP_BACKUP_GID:-0}"

case "$KEEP_DAYS" in
  ''|*[!0-9]*) echo "openbp-backup: OPENBP_BACKUP_KEEP_DAYS має бути цілим ≥ 0, отримано '$KEEP_DAYS'" >&2; exit 2 ;;
esac

stamp="$(date +%Y%m%d-%H%M%S)"
file="$DIR/openbp-${PGDATABASE:-openbp}-$stamp.dump"
partial="$file.partial"

# Дамп містить хеші паролів і зашифровані credentials — лише власнику.
umask 077
mkdir -p "$DIR"
if ! pg_dump --format=custom --no-owner --file="$partial"; then
  rm -f "$partial"
  echo "$(date -Iseconds) openbp-backup: ПОМИЛКА pg_dump, бекап не створено" >&2
  record false "pg_dump завершився помилкою"
  exit 1
fi
mv "$partial" "$file"
chown "$OWNER" "$file" 2>/dev/null || true

removed=0
if [ "$KEEP_DAYS" -gt 0 ]; then
  for old in $(find "$DIR" -maxdepth 1 -name 'openbp-*.dump' -type f -mmin +$((KEEP_DAYS * 1440))); do
    rm -f "$old" && removed=$((removed + 1))
  done
fi

size="$(wc -c < "$file")"
record true "$(basename "$file") ($size байт)"
echo "$(date -Iseconds) openbp-backup: ok $file ($size байт), видалено старих: $removed"
