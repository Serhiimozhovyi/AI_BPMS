# Архітектура OpenBP_builder

## 1. Компоненти

```
            ┌────────────┐   REST/WS    ┌────────────┐
 Браузер ──►│  apps/web  │─────────────►│  apps/api  │◄── зовнішні webhook-и
            └────────────┘              └─────┬──────┘
                                              │ ставить job у чергу
                                              ▼
                     ┌──────────┐      ┌────────────┐
                     │ Postgres │◄────►│   Redis    │
                     └────▲─────┘      │  (BullMQ)  │
                          │            └─────┬──────┘
                          │                  │ бере job
                          │            ┌─────▼──────┐
                          └────────────┤ apps/worker│── packages/engine + packages/nodes
                                       └────────────┘
```

- **apps/api** — CRUD сценаріїв і credentials, запуск, журнал, авторизація; приймає webhook-и (`/webhook/:path`); реєструє розклади (BullMQ repeatable jobs).
- **apps/worker** — бере job `execute-workflow`, завантажує сценарій, запускає `engine`, пише результат кожного кроку в БД.
- **apps/web** — SPA: список сценаріїв, канвас, журнал, credentials, налаштування. Живе оновлення тестового запуску — через WebSocket (або SSE) з api.
- **packages/engine** — чиста бібліотека виконання графа.

## 2. Модель виконання (як у n8n, але своя реалізація)

- Дані між вузлами — масив **items**: `Array<{ json: Record<string, unknown>; binary?: Record<string, BinaryRef> }>`.
- Вузол отримує items з кожного вхідного порту, повертає items на кожен вихідний порт (`outputs: Item[][]`). IF має 2 виходи (true/false), Switch — N.
- Виконання — **черга готових вузлів** (а не рекурсія): вузол стає готовим, коли на всіх його обов'язкових входах є дані (Merge чекає обидва входи; інші вузли — будь-який).
- Вузол з порожнім входом за замовчуванням не виконується (гілка «згасає»), крім вузлів з `alwaysRun: true`.
- Після кожного вузла двигун викликає `store.saveStep(...)` — стан можна відновити і продовжити після падіння воркера.
- Помилка вузла: retry за налаштуваннями вузла → якщо не вдалось і `continueOnFail` — помилка йде як item на вихід; інакше виконання падає і запускається error-workflow.

## 3. Вирази

- Синтаксис: `{{ <js-вираз> }}` у будь-якому рядковому параметрі.
- Доступні змінні: `$json` (поточний item), `$input`, `$node["Назва"].json`, `$now`, `$env` (тільки дозволені змінні), `$execution.id`.
- Обчислення — у `packages/expressions` через QuickJS, ліміт 50 мс і 16 МБ на вираз.

## 4. Схема БД (Drizzle, Postgres)

| Таблиця | Ключові поля |
|---|---|
| `users` | id, email, password_hash, role, created_at |
| `workflows` | id, name, active, current_version_id, created_by, updated_at |
| `workflow_versions` | id, workflow_id, version, definition (jsonb), created_by, created_at |
| `credentials` | id, name, type, data_encrypted (bytea), iv, created_by |
| `executions` | id, workflow_id, version_id, mode (manual/webhook/schedule/retry), status (queued/running/success/error/canceled), started_at, finished_at, error |
| `execution_steps` | id, execution_id, node_id, run_index, status, input (jsonb), output (jsonb), error, started_at, finished_at |
| `webhooks` | id, workflow_id, node_id, method, path (унікальний) |
| `schedules` | id, workflow_id, node_id, cron, timezone, bullmq_key |

Великі бінарні дані (файли) — не в jsonb, а у файлове сховище (локальна папка / S3-сумісне) з посиланням `BinaryRef`.

## 5. Безпека

- Credentials: AES-256-GCM, ключ `OPENBP_ENCRYPTION_KEY`; розшифровка тільки у воркері в момент виконання вузла.
- Code-вузол і вирази — тільки QuickJS, без доступу до файлової системи, мережі та процесу.
- Webhook-и: опційна автентифікація (header-токен / Basic).
- Ролі перевіряються на рівні API (не лише в UI).

## 6. Готові компоненти, які використовуємо

| Задача | Бібліотека | Ліцензія |
|---|---|---|
| Канвас | @xyflow/react | MIT |
| Черга, розклади | bullmq | MIT |
| Пісочниця | quickjs-emscripten | MIT |
| Форми зі схеми | @rjsf/core | Apache-2.0 |
| Редактор коду | monaco-editor | MIT |
| UI | shadcn/ui, Tailwind | MIT |
| ORM | drizzle-orm | Apache-2.0 |
| Ідеї та код інтеграцій | Activepieces pieces | MIT (з атрибуцією) |
| Ідеї UX | n8n | лише ідеї, без коду |
