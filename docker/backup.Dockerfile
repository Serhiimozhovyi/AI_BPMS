# Сервіс бекапів docker-compose.prod.yml (F4-11): pg_dump тієї ж мажорної версії, що й БД, і busybox crond.
FROM postgres:16-alpine
COPY docker/backup/openbp-backup.sh /usr/local/bin/openbp-backup
COPY docker/backup/entrypoint.sh /usr/local/bin/openbp-backup-cron
RUN chmod 755 /usr/local/bin/openbp-backup /usr/local/bin/openbp-backup-cron
# crond працює від root (власник файлів — OPENBP_BACKUP_UID/GID), PID 1 — щоб завдання писали в журнал контейнера.
ENTRYPOINT ["/usr/local/bin/openbp-backup-cron"]
