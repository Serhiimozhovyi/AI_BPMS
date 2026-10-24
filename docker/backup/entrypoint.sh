#!/bin/sh
# Розклад бекапів (F4-11): busybox crond запускає openbp-backup за OPENBP_BACKUP_CRON (5 полів crontab,
# часовий пояс TZ). Вивід завдання йде в журнал контейнера (docker compose logs backup).
set -eu

CRON="${OPENBP_BACKUP_CRON:-0 2 * * *}"
if [ "$(echo "$CRON" | wc -w)" -ne 5 ]; then
  echo "openbp-backup: OPENBP_BACKUP_CRON має містити 5 полів crontab, отримано '$CRON'" >&2
  exit 2
fi

mkdir -p /etc/crontabs
echo "$CRON /usr/local/bin/openbp-backup > /proc/1/fd/1 2> /proc/1/fd/2" > /etc/crontabs/root
echo "openbp-backup: розклад '$CRON' (TZ=${TZ:-UTC}), тека ${OPENBP_BACKUP_DIR:-/backups}, зберігати ${OPENBP_BACKUP_KEEP_DAYS:-14} дн."
exec crond -f -l 8
