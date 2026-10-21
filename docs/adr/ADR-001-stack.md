# ADR-001. Технологічний стек: .NET + Angular

Статус: **прийнято** · Дата: 2026-10-21 · Замінює: стек з CLAUDE.md v0.1 (TypeScript)

## Контекст

Перша версія документів (21.10.2026) описувала TypeScript-стек: Fastify, Drizzle, BullMQ, React Flow.
Того ж дня прийнято два продуктових рішення, які змінюють вхідні дані:

1. OpenBP — **продукт на продаж і для команди**, не особистий інструмент.
2. Команда, яка підтримуватиме продукт після MVP, — **.NET + Angular**.

Автор MVP (власник продукту) будує сам разом із Claude Code, тому мова має значення для
Claude і для команди через рік, а не для автора зараз. Коду в репо ще немає — ціна зміни ≈ 0.

## Рішення

| Шар | Вибір | Ліцензія | Чому |
|---|---|---|---|
| Runtime | .NET 10 LTS, C# 14 | MIT | LTS до 2028, команда знає |
| API | ASP.NET Core Minimal APIs + FluentValidation | MIT / Apache-2.0 | мінімум магії, легко читати агенту |
| ORM / БД | EF Core + Npgsql, PostgreSQL 16 | MIT / PostgreSQL | міграції «з коробки», jsonb |
| Черга виконань | власна таблиця `jobs` у Postgres, `FOR UPDATE SKIP LOCKED` | — | один сервіс в інфраструктурі замість двох; Redis не потрібен для MVP |
| Розклад (cron) | Quartz.NET (Postgres job store) | Apache-2.0 | перевірений, кластеризується |
| Движок виконання | **власний** `OpenBP.Engine` (чиста бібліотека, дизайн з `architecture.md`) | — | див. «Альтернативи» |
| MCP клієнт і сервер | `ModelContextProtocol` (офіційний C# SDK, Anthropic + Microsoft) | MIT | обидва напрямки одним пакетом |
| Пісочниця виразів і Code-вузла | Jint (JS-інтерпретатор у .NET) з лімітами часу, пам'яті, глибини рекурсії | BSD-2 | нема нативного процесу, лімити вбудовані |
| Секрети | AES-256-GCM, ключ з `OPENBP_ENCRYPTION_KEY`; ротація через `ASP.NET Data Protection` | — | як у v0.1 |
| Auth | ASP.NET Identity (email+пароль) → OIDC (Entra ID) у фазі 4 | MIT | — |
| Frontend | Angular 20+, standalone components, Signals | MIT | команда |
| Канвас | **spike у фазі 2**: ngx-vflow (Angular-native) або Rete.js v2 (має Angular-плагін) | MIT / MIT | обидва MIT, обрати за результатом 1-денного спайку |
| Форми зі схеми | ngx-formly + JSON Schema | MIT | генерація форми вузла зі схеми MCP tool |
| Редактор коду | Monaco (ngx-monaco-editor-v2) | MIT | — |
| UI-кіт | Angular Material + Tailwind | MIT | — |
| Тести | xUnit + Testcontainers (Postgres); Vitest для Angular; Playwright e2e | — | — |
| Деплой | Docker Compose: `postgres`, `api`, `worker`, `web` (nginx) | — | один сервер для self-hosted клієнта |
| Монорепо | `OpenBP.sln` + `apps/web` (Angular) в одному репо | — | один PR на фічу наскрізь |

Версії бібліотек фіксуються при старті фази 0 після перевірки актуальних релізів; у цьому ADR — лише вибір.

## Альтернативи, які відкинуто

**Залишити TypeScript (v0.1).** Швидше до демо; можна переносити pieces з Activepieces (MIT).
Відкинуто: команда не зможе підтримувати; в MCP-native архітектурі (ADR-002) конектори приходять
через MCP-сервери, тому цінність бібліотеки pieces падає.

**Elsa Workflows 3 як движок.** MIT, є дизайнер. Відкинуто для MVP: приносить власну модель
workflow і UI, які конфліктують з нашим `workflow-format.md` та власним канвасом; ризик «боротьби
з фреймворком». Повернутись, якщо власний движок не витримає вимог фази 3 (паузи на людину, довгі очікування).

**Temporal (.NET SDK).** Найнадійніше durable execution. Відкинуто для MVP: окремий кластер
Temporal у Docker Compose для клієнта з одним сервером — надмірно. Кандидат №1 для фази «enterprise».

**Hangfire** для черги. LGPL-3 для Core — небажано для продукту на продаж.

**ClearScript (V8)** для пісочниці. Потужніше за Jint, але тягне нативний V8 у кожен образ; Jint достатньо для виразів і невеликого Code-вузла.

## Наслідки

- CLAUDE.md, architecture.md, node-sdk.md (→ connectors.md), roadmap.md переписані під це рішення.
- Усе, що в v0.1 посилалось на Activepieces як джерело коду, знято; `THIRD_PARTY_NOTICES.md` лишається для майбутніх запозичень.
- Angular-частина і .NET-частина живуть в одному репо та одному PR, щоб нічний агент закривав фічу наскрізь.
