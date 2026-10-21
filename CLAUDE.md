# OpenBP_builder — правила проєкту для Claude Code

OpenBP_builder — платформа автоматизації процесів у стилі n8n: тригер → граф вузлів → інтеграції.
Ми не пишемо все з нуля: беремо перевірені open-source бібліотеки і пишемо лише «клей», двигун і UX.

Перед будь-якою задачею прочитай відповідні файли в `docs/`:
- `docs/PRD.md` — що будуємо і для кого, сценарії MVP
- `docs/architecture.md` — компоненти, потоки даних, схема БД
- `docs/workflow-format.md` — формат workflow JSON (джерело правди для двигуна і канвасу)
- `docs/node-sdk.md` — контракт вузла
- `docs/roadmap.md` — етапи, поточний етап, критерії «готово»

## Стек (не змінювати без погодження)

- Мова: TypeScript (strict) скрізь
- Монорепо: pnpm workspaces + Turborepo
- Backend API: Fastify + Zod (валідація), Drizzle ORM, PostgreSQL 16
- Черга: BullMQ + Redis 7
- Frontend: React 18 + Vite, React Flow (`@xyflow/react`), Tailwind + shadcn/ui, TanStack Query, Zustand
- Форми вузлів: JSON Schema → `@rjsf/core` (або власний рендерер, якщо rjsf заважає)
- Редактор коду: Monaco
- Пісочниця для коду і виразів: `quickjs-emscripten`
- Тести: Vitest (unit), Playwright (e2e для web)
- Локальний запуск: Docker Compose (postgres, redis, api, worker, web)

## Структура

```
apps/
  api/        # REST API + webhook-ендпоінти + тригери (Fastify)
  worker/     # виконання workflow з черги (BullMQ)
  web/        # редактор-канвас, журнал виконань, credentials (React)
packages/
  engine/     # виконання графа: чистий TS, без БД і мережі напряму
  nodes-sdk/  # типи і хелпери для опису вузлів
  nodes/      # реалізації вузлів (кожен вузол — окрема папка)
  expressions/# парсер і обчислення {{ }} у пісочниці
  db/         # схема Drizzle, міграції
  shared/     # спільні типи, zod-схеми workflow JSON
docs/
```

## Команди

- `pnpm install`
- `pnpm dev` — усі застосунки в dev-режимі
- `pnpm test` — усі тести
- `pnpm lint && pnpm typecheck` — запускати перед кожним завершенням задачі
- `docker compose up -d postgres redis` — інфраструктура для локальної розробки
- `pnpm --filter @openbp/db migrate` — міграції

## Правила

1. `packages/engine` не знає про БД, HTTP чи Redis. Усе зовнішнє — через інтерфейси (`ExecutionStore`, `CredentialProvider`, `Logger`), які передаються ззовні. Так двигун тестується без інфраструктури.
2. Формат workflow описаний у `docs/workflow-format.md` і продубльований zod-схемою в `packages/shared`. Змінюєш формат — оновлюй обидва місця і додавай міграцію версії формату.
3. Кожен вузол: опис (схема параметрів) + `execute()` + тести. Без тестів вузол не вважається готовим.
4. Користувацький код (Code-вузол, вирази `{{ }}`) виконується ТІЛЬКИ в пісочниці QuickJS з лімітами часу і пам'яті. Ніяких `eval`, `new Function`, `vm` у Node.
5. Секрети (credentials) шифруються AES-256-GCM, ключ — зі змінної `OPENBP_ENCRYPTION_KEY`. Секрети ніколи не потрапляють у логи, журнал виконань чи відповіді API.
6. Код n8n не копіюємо (ліцензія Sustainable Use). Ідеї й UX — можна. Код з Activepieces (MIT) — можна, з атрибуцією в `THIRD_PARTY_NOTICES.md`.
7. Інтерфейс — українською за замовчуванням, тексти через i18n-ключі (щоб пізніше додати англійську).
8. Одна задача — один невеликий PR/коміт з осмисленим повідомленням. Не роби рефакторинг «заодно».
9. Якщо специфікація в `docs/` суперечить сама собі або чогось бракує — зупинись і запитай, а не вигадуй.

## Визначення «готово» для будь-якої задачі

- код проходить `pnpm lint`, `pnpm typecheck`, `pnpm test`
- нова поведінка покрита тестами
- якщо змінено контракт чи формат — оновлено `docs/`
- у `docs/roadmap.md` позначено виконаний пункт
