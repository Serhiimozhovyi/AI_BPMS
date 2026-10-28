# Changelog

Усі помітні зміни OpenBP. Формат — [Keep a Changelog](https://keepachangelog.com/uk/1.1.0/); записи додаються зверху.

## [Unreleased]

### Додано

- MCP `create_table` і `alter_table` (F3-109): зміна структури таблиць у два виклики — план без запису, потім застосування з `planId`; `IDataTableService.PlanCreateAsync/PlanAlterAsync/Apply…`.
- Таблиці проєкту (F8-02): таблиця належить проєкту або спільна, права — права проєкту; типи колонок `datetime`, `enum`, `ref`, `user`, чутливі колонки з аудитом читання; обмеження `required` і `unique` з умовою; операції `insertIf`, `increment`, `count` у вузлі «Таблиця даних»; `projectId` у `/tables`, `list_tables`, `query_table` і ресурс `openbp://projects/{id}/tables`; на сторінці «Таблиці» — вибір проєкту, нові типи й правила.
- ADR-005 «Проєкт як робочий простір» і контракт `docs/projects.md`; фаза 8 F8-01…F8-12 у `BACKLOG.md` (F8-01, 2026-10-28).
- Фундамент (фаза 0): скелет .NET-рішення (F0-01), скелет Angular (F0-02), Docker Compose для розробки (F0-03), CI на GitHub Actions (F0-04).
- `OpenBP.Shared`: моделі формату workflow і JSON Schema (F1-01).
- `OpenBP.Engine`: інтерфейси движка (`IExecutionStore`, `ICredentialProvider`, `INodeRegistry`, `IExpressionEvaluator`, `IExecutionLogger`, `IBinaryStore`), контракт вузла `INode`/`INodeContext`; `OpenBP.Engine.Testing`: in-memory реалізації для тестів і CLI (F1-03).
- ADR-003: канвас на ngx-vflow за результатами спайку F1-11; прототип у `apps/web/src/app/canvas/`.
- Перевірка протоколу нічного агента: цей changelog додано автоматично за задачею F0-05 (2026-10-22).
