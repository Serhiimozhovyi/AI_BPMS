# Психологічна підтримка: застосунок-адмінка і MCP-конектор

Задачі F3-33 (цей документ), F3-34 (бекенд і MCP-tools), F3-35 (сторінка для психологів), F3-31 (сценарій бота в OpenBP).
Джерело вимог: n8n-сценарій «Астарта · Бот психологічної підтримки» (91 вузол, дані в Google Sheets) і пов'язаний з ним
«Астарта · Шедулер». Рішення власника від 2026-10-24: дані живуть в окремому застосунку з власним бекендом і сторінкою
адміністрування, а в OpenBP він підключається як MCP-конектор (CLAUDE.md, правило 3). Ядро OpenBP не змінюється.

Розділ 10 — рішення, які я ухвалив сам, бо власник доручив виконувати задачі без зупинок. Кожне з них можна змінити до
F3-34; що зміниться в такому разі, там теж записано.

## 1. Межі: що де живе

| Що | Де | Чому |
|---|---|---|
| Дані (люди, психологи, слоти, записи, події, БСТ, черга, матеріали, тексти, лічильники) | `psy-support`, власна БД | дані з персональною інформацією — в одному місці з чіткими правами доступу |
| Бізнес-правила (ліміти, місткість, черга, вікно скасування) | `psy-support`, у транзакціях БД | правило має бути атомарним; у n8n воно трималось на «перечитати таблицю і відкотити» |
| Екрани бота (текст і кнопки) | `psy-support`, у відповідях tools | тексти беруться з `settings`, кнопки несуть id з цієї ж БД (§6.1) |
| Транспорт Telegram (webhook, надсилання, редагування, `answerCallbackQuery`, команди) | OpenBP + `connectors/telegram` (F3-27) | інтеграції лише через MCP; `psy-support` не знає токена бота |
| Маршрут апдейта за розділами, нагадування за розкладом, розсилки, журнал виконань | сценарії OpenBP (F3-31 і «Шедулер») | видно на канвасі, перезапускається, має історію виконань |
| Вхід психологів і координатора в адмінку | `psy-support` | окремий застосунок зі своїми ролями (§7) |

`psy-support` ніколи сам не надсилає повідомлень у Telegram. Коли дія когось сповіщає (психолога про новий запис, першого
в черзі про звільнене місце, учасників про оголошення), tool бота повертає список `notifications`, а сценарій OpenBP
надсилає їх через Telegram-конектор. Сповіщення від дій в адмінці (координатор скасував запис, психолог надіслав
оголошення) потрапляють у таблицю `outbox`, і «Шедулер» OpenBP забирає їх разом із нагадуваннями (`due_notifications`).

## 2. Розташування і стек

- `connectors/psy-support`: створено з шаблону `dotnet new openbp-connector` (Connector Kit), має власний sln і
  `Directory.*.props`, лежить поза `OpenBP.sln`. Його збирає й тестує CI job «Конектори» (matrix `psy-support`).
- Стек той самий, що в OpenBP (ADR-001): .NET 10, ASP.NET Core Minimal APIs, EF Core + Npgsql, PostgreSQL 16, xUnit +
  Testcontainers; MCP — офіційний C# SDK `ModelContextProtocol`. Це не новий стек і не потребує ADR.
- Один процес, три входи:
  - `/mcp` — Streamable HTTP для OpenBP і ШІ-агентів; той самий набір tools — через stdio (`dotnet …dll` без `--http`);
  - `/api/v1/*` — REST для сторінки адмінки;
  - `/` — Angular SPA адмінки (F3-35), статика з `wwwroot`.
- Frontend адмінки (F3-35) — `connectors/psy-support/web`: Angular (standalone, Signals), Angular Material + Tailwind,
  дизайн-система AC-Kit з Figma (лише читання). Окремий застосунок, а не частина `apps/web`, бо в нього свої
  користувачі, ролі й домен.
- БД — окрема база `psy_support` з власним користувачем. Для розгортання поруч з OpenBP це той самий сервер Postgres із
  `docker-compose.yml`, але без спільних таблиць і без доступу OpenBP до цієї бази.
- Конфігурація: `ConnectionStrings__PsySupport`, `PSY_MCP_TOKEN` (§7.3), `PSY_ADMIN_EMAIL`/`PSY_ADMIN_PASSWORD`
  (перший координатор, як в OpenBP), `PSY_TIMEZONE` (типово `Europe/Kyiv`).

## 3. Модель даних

Усі моменти часу зберігаються як `timestamptz` (UTC). У `Europe/Kyiv` вони переводяться лише під час показу й під час
розбору вводу. Тексти статусів — англійські enum-рядки в БД, а українською їх показують бот і адмінка. Ключі —
`bigint` identity, крім `users.telegram_id` і `settings.key`: короткі числа поміщаються в `callback_data` (§6.1).

| Таблиця | Колонки | Обмеження й індекси |
|---|---|---|
| `settings` | `key` PK, `value` text, `updated_at`, `updated_by` | ключі з §3.1 |
| `users` | `telegram_id` bigint PK, `name`, `phone` (`380XXXXXXXXX`), `registered_at`, `deleted_at` | `phone` — унікальний серед не видалених |
| `psychologists` | `id`, `name`, `phone`, `email`, `telegram_id` null, `active` bool, `created_at` | `telegram_id` унікальний; прив'язується, коли людина ділиться номером, що збігається з `phone` |
| `staff_accounts` | `id`, `email`, `password_hash`, `role` (`coordinator` / `psychologist`), `psychologist_id` null, `disabled_at`, `last_login_at` | вхід в адмінку (§7.1); `email` унікальний |
| `slots` | `id`, `psychologist_id`, `starts_at`, `duration_min`, `format`, `status` (`open` / `closed`), `source` (`template` / `manual`), `created_at` | `(psychologist_id, starts_at)` унікальний |
| `bookings` | `id`, `slot_id`, `telegram_id`, `status` (`active` / `cancelled` / `completed` / `no_show`), `created_at`, `cancelled_at`, `cancelled_by` (`user` / `psychologist` / `coordinator`), `moved_from_id` null | **частковий унікальний індекс `slot_id WHERE status = 'active'`**: один слот — один активний запис |
| `events` | `id`, `title`, `description`, `starts_at`, `format`, `host`, `contact`, `capacity` (0 = без ліміту), `status` (`draft` / `open` / `closed`), `notice`, `notice_sent_at` | — |
| `event_registrations` | `id`, `event_id`, `telegram_id`, `status` (`active` / `cancelled` / `attended`), `created_at`, `feedback` 1–5 null | `(event_id, telegram_id)` унікальний; повторна реєстрація оновлює рядок |
| `bst_sessions` | `id`, `psychologist_id`, `starts_at`, `format`, `capacity`, `status` (`open` / `closed`), `notice`, `notice_sent_at` | — |
| `bst_bookings` | `id`, `session_id`, `telegram_id`, `status` (`active` / `cancelled` / `attended`), `created_at`, `moved_from_id` null | `(session_id, telegram_id)` унікальний |
| `waitlist` | `id`, `session_id`, `telegram_id`, `status` (`waiting` / `offered` / `cancelled` / `booked`), `added_at`, `offered_at` | `(session_id, telegram_id)` унікальний серед `waiting`/`offered` |
| `materials` | `id`, `title`, `body`, `link`, `status` (`published` / `archived` / `draft`), `published_at`, `views` | — |
| `reminders` | `id`, `kind` (`booking` / `event` / `bst`), `target_id`, `offset_min` (1440 / 60 / 10), `sent_at` | `(kind, target_id, offset_min)` унікальний — замість колонок `r1440/r60/r10` |
| `slot_templates` | `id`, `psychologist_id`, `weekday`, `time`, `duration_min`, `format`, `valid_from`, `valid_to` | для генерації слотів «Шедулером» |
| `outbox` | `id`, `chat_id`, `text`, `reply_markup` jsonb null, `created_at`, `sent_at` null | сповіщення від дій в адмінці (§1) |
| `invites` | `id`, `staff_account_id`, `token_hash`, `expires_at`, `used_at` | одноразові посилання для встановлення пароля (§7.1) |
| `counters` | `metric` PK, `value` bigint | атомарний `UPDATE … SET value = value + 1` |
| `audit_log` | `id`, `at`, `actor` (`bot` / `staff:<id>` / `system`), `action`, `entity`, `entity_id`, `details` jsonb | без телефонів, імен і `telegram_id` у `details` (§8) |

Колонки `taken` (подій і БСТ) не зберігаються: це `count(*)` активних записів. Колонка `key` (`pid|date|time`) теж
не потрібна, бо слот має `id`.

### 3.1. Налаштування (`settings`)

Тексти: `TEXT_WELCOME`, `TEXT_MENU`, `TEXT_SOS`, `TEXT_HELP`, `TEXT_BST_INFO`. Числа: `MIN_HOURS_BEFORE` (24),
`HORIZON_DAYS` (21), `MAX_ACTIVE_CONSULTS` (2), `SLOT_DURATION_MIN` (50), `BST_CAPACITY_DEFAULT` (8),
`PSY_PEER_VIEW_DAYS` (14). Тексти редагує координатор в адмінці; бот бере їх без перезапуску.

## 4. Правила

Час рахується в `PSY_TIMEZONE` (`Europe/Kyiv`). «Зараз» береться з `TimeProvider`, тож тести детерміновані.

1. **Видимість слотів.** Слот видно, якщо `status = open`, психолог `active`, немає активного запису, а
   `starts_at` лежить у межах [`зараз + MIN_HOURS_BEFORE` год; `зараз + HORIZON_DAYS` днів].
2. **Запис на консультацію** (`book_slot`) — одна транзакція:
   - перевірка видимості слота (правило 1);
   - перевірка ліміту: менше `MAX_ACTIVE_CONSULTS` активних майбутніх записів людини (лічимо під `SELECT … FOR UPDATE`
     рядка `users`, щоб два одночасні записи однієї людини не обійшли ліміт);
   - `INSERT bookings`. Одночасний запис двох людей на той самий слот: перший `INSERT` проходить, другий ловить порушення
     часткового унікального індексу → результат `slot_taken` («Цей час щойно зайняли»), без винятку назовні.
3. **Скасування** (`cancel_booking`) дозволене, якщо до `starts_at` більше ніж `MIN_HOURS_BEFORE` год. Інакше —
   `too_late` з `email` психолога в тексті екрана.
4. **Перенесення** (`move_booking`) — одна транзакція: новий запис за правилом 2 (ліміт рахується без старого запису),
   потім скасування старого за правилом 3 з `moved_from_id`. Якщо будь-який крок не вдається, нічого не змінюється.
5. **Події** (`register_event`): у транзакції `SELECT … FOR UPDATE` рядка `events`, потім перевірка `status = open` і
   `capacity = 0 OR активних < capacity`, потім upsert реєстрації. Порядок «запис → перевірка → відкат» з n8n не потрібен.
6. **БСТ** (`book_bst`) — так само, як події, з `bst_sessions.capacity`. Місця немає → `full` з кнопкою «У лист
   очікування».
7. **Черга** (`join_waitlist`, `cancel_bst`): коли активний запис на сеанс скасовують, перший `waiting` за `added_at`
   переходить в `offered` (`offered_at = зараз`) і потрапляє в `notifications` («Звільнилось місце…» з кнопкою
   «Записатися»). У запис автоматично не переводимо, як і в n8n. `book_bst` людини зі статусом `offered` переводить її
   рядок черги в `booked`.
8. **Кабінет психолога** відкривається, лише якщо `telegram_id` прив'язаний до психолога з `active = true`; перевірка
   відбувається на кожному виклику tool'а кабінету. Психолог бачить записи всіх психологів на `PSY_PEER_VIEW_DAYS` днів
   уперед (як у n8n), але телефон бачить лише у своїх записах.
9. **Оголошення** (`send_event_notice`, `send_bst_notice`): повертає `notifications` усім активним учасникам і ставить
   `notice_sent_at`. Повторний виклик без зміни `notice` нічого не надсилає.
10. **Нагадування й черга сповіщень** (`due_notifications`): записи, події й БСТ, для яких `starts_at - offset_min` уже
    минуло, `starts_at` ще ні, а рядка в `reminders` немає, плюс не надіслані рядки `outbox` → список з `id` вигляду
    `r:<kind>:<target>:<offset>` або `o:<outbox id>`. Після надсилання `mark_notifications_sent { ids }` вставляє рядки
    `reminders` і ставить `outbox.sent_at` (ідемпотентно).
11. **Посилання «додати в календар»** (Outlook, Google) мають нейтральну назву «Особиста зустріч».

## 5. MCP-tools

Усі tools мають `outputSchema` (`UseStructuredContent`). Помилки домену (`slot_taken`, `too_late`, `limit_reached`,
`full`, `not_psychologist`…) — це **не** `isError`, а успішний результат з `ok: false`, `code` і екраном для людини:
сценарій бота показує екран у будь-якому разі. `isError` повертається лише для збоїв (БД недоступна, некоректні
аргументи).

Спільна форма результату дій бота:

```json
{
  "ok": true,
  "code": "booked",
  "screen": { "text": "<b>Записано</b> на 12.11, 10:00…", "parse_mode": "HTML", "reply_markup": { "inline_keyboard": [[…]] } },
  "notifications": [ { "chat_id": 123456, "text": "Новий запис: 12.11, 10:00", "reply_markup": null } ],
  "commands": null
}
```

`screen` іде прямо в `send_message`/`edit_message_text` Telegram-конектора (F3-27). `notifications` сценарій надсилає
по одному. `commands` (лише після реєстрації психолога) — аргументи для `set_my_commands` зі `scope` його чату (`/psy`).

### 5.1. Для бота: екрани за розділами

Сценарій OpenBP розбирає апдейт (повідомлення, контакт або `callback_query`), визначає розділ за префіксом
`callback_data` (`core.switch` версії 2, 7 гілок — F3-29) і викликає один tool розділу:

| Tool | Розділ | Аргументи |
|---|---|---|
| `bot_start` | `/start`, контакт, «Головне меню» | `telegram_id`, `name`, `contact?` (`user_id`, `phone_number`) |
| `bot_menu` | `m:psy`, `m:help`, `m:sos` | `telegram_id`, `data` |
| `bot_materials` | `m:mat`, `mt:*` | `telegram_id`, `data` |
| `bot_consult` | `m:cons`, `c:*` | `telegram_id`, `data` |
| `bot_events` | `m:ev`, `e:*` | `telegram_id`, `data` |
| `bot_bst` | `m:bst`, `b:*` | `telegram_id`, `data` |
| `bot_cabinet` | `/psy`, `p:*` | `telegram_id`, `data` |

Кожен з них розбирає `data` (коди кнопок, які сам і згенерував), викликає доменну операцію з §5.2 і повертає форму
вище. Застаріла або невідома кнопка дає `code: stale` і головне меню, а не помилку. Незареєстрована людина в будь-якому
розділі отримує екран реєстрації (запит номера через `request_contact`).

### 5.2. Доменні операції (для ШІ-агентів, адмінки і тестів)

Ті самі правила, що й у §4. Ці tools не мають тексту, лише дані. Tools бота з §5.1 — обгортки над ними.

- Люди: `register_user`, `get_user`.
- Консультації: `list_psychologists`, `list_slots { psychologist_id?, from?, to? }`, `book_slot`, `cancel_booking`,
  `move_booking`, `list_bookings { telegram_id? | psychologist_id?, from?, to? }`.
- Події: `list_events`, `register_event`, `cancel_event_registration`, `rate_event`.
- БСТ: `list_bst_sessions`, `book_bst`, `cancel_bst`, `join_waitlist`, `leave_waitlist`, `move_bst`.
- Матеріали: `list_materials { page, archived? }`, `get_material` (збільшує `views`).
- Розклад і розсилки: `due_notifications`, `mark_notifications_sent { ids[] }`, `generate_slots { days }` (з
  `slot_templates`), `anonymize_old_records` (§8), `send_event_notice`, `send_bst_notice`.
- Аналітика: `get_counters`.

Записувальні tools мають анотацію `Destructive = false` і `Idempotent` там, де це правда (`mark_notifications_sent`,
`rate_event`). Персональні дані (§8): `list_bookings` віддає телефон, лише якщо виклик іде від імені психолога цього
запису (у бота) або токена з правом `pii` (§7.3).

## 6. Бот у OpenBP (F3-31)

```
core.webhook (X-Telegram-Bot-Api-Secret-Token)
  → core.code «Розбір»: message | callback_query → { telegram_id, name, data, contact, message_id, callback_id }
  → core.switch v2 «Розділ» (7 правил за префіксом data)
  → mcp.tool psy-support: bot_start | bot_menu | bot_materials | bot_consult | bot_events | bot_bst | bot_cabinet
  → «Відповідь»: answer_callback_query (якщо кнопка) → edit_message_text (якщо кнопка) або send_message
  → core.loop notifications → send_message
  → set_my_commands (якщо commands)
```

Окремий сценарій «Шедулер»: `core.schedule` (щохвилини) → `due_notifications` → цикл `send_message` →
`mark_notifications_sent`; `core.schedule` (щоночі) → `generate_slots` і `anonymize_old_records`.

### 6.1. Коди кнопок

`callback_data` має до 64 байт, тому кнопки несуть числові id (`bigint`, §3). Схема префіксів лишається з n8n
(`m:`, `mt:`, `c:`, `e:`, `b:`, `p:`), а `<key>` замінюється на id: `c:ok:1834`, перенесення `c:mo:1834:2051`, дата —
`yyyyMMdd`. Найдовший код займає менше 30 байт.

## 7. Ролі, вхід і доступ

### 7.1. Адмінка (люди)

| Роль | Бачить і робить |
|---|---|
| `coordinator` | усе: психологи й облікові записи, тексти бота (`settings`), слоти й шаблони, події, БСТ, матеріали, оголошення, лічильники, журнал; імпорт (§9) |
| `psychologist` | свій розклад і шаблони, записи до себе (з телефонами), записи колег на `PSY_PEER_VIEW_DAYS` днів (без телефонів), події й сеанси БСТ, які веде (учасники, оголошення), матеріали (читання) |

- Вхід — email і пароль, cookie-сесія. Як і в OpenBP (`architecture.md` §10.6), з ASP.NET Core Identity беруться лише
  `PasswordHasher<T>` і cookie-схема.
- Обліковий запис психолога створює координатор. Психолог задає пароль за одноразовим посиланням, яке діє 24 год.
  Посилання показується координатору в адмінці, бо пошти застосунок не надсилає.
- Перший координатор створюється під час старту з `PSY_ADMIN_EMAIL`/`PSY_ADMIN_PASSWORD`, якщо облікових записів немає.
- 5 невдалих входів поспіль блокують вхід на 15 хв. Сесія закінчується через 12 год бездіяльності.

### 7.2. Кабінет у боті

Кабінет у боті не має пароля. Доступ визначає прив'язка `telegram_id` до активного психолога (правило 4.8), бо
Telegram підтверджує `from.id`. Обліковий запис в адмінці й кабінет у боті — дві незалежні речі.

### 7.3. MCP

- HTTP `/mcp` вимагає `Authorization: Bearer <PSY_MCP_TOKEN>`. В OpenBP це credential конектора
  `{ "headers": { "Authorization": "Bearer …" } }` (`architecture.md` §10.7). Без токена — 401 до MCP-обміну.
- Токенів два: `PSY_MCP_TOKEN` (бот, ШІ-агенти; телефони лише в межах правила 4.8) і `PSY_MCP_TOKEN_PII` (повний
  доступ до телефонів; для адмін-сценаріїв, за замовчуванням не задано).
- stdio не потребує токена: процес запускає той, хто має доступ до конфігурації OpenBP.

## 8. Персональні дані

Що є персональними даними: телефон, ім'я, `telegram_id`, факт запису до психолога (це чутливіше за телефон), оцінки
подій.

- **Журнали.** `audit_log` і журнали застосунку (`ILogger`) не містять телефонів, імен і `telegram_id`, лише id сутностей
  і коди дій. Як у n8n-боті: «дії з консультаціями — без `telegram_id`». Запити EF Core не логуються з параметрами.
  Тест F3-34 перевіряє журнали після повного сценарію запису.
- **OpenBP.** Відповіді tools потрапляють в `execution_steps` OpenBP. Тому tools бота не повертають телефон у `screen`
  нікому, крім психолога цього запису. `notifications` психологу містять ім'я і час, але не телефон: його видно в
  кабінеті. Сценарій бота в OpenBP варто налаштувати на коротке зберігання історії виконань (F4-B).
- **Доступ.** Телефони видно лише у своїх записах психолога і координатору (§7.1). Експорт CSV з телефонами — лише для
  координатора, і він пишеться в `audit_log`.
- **Видалення.** Людина може попросити видалити дані: координатор натискає «Видалити користувача». `users.phone` і `name`
  стираються, ставиться `deleted_at`, активні записи скасовуються. Знеособлені записи лишаються для лічильників.
- **Зберігання.** Записи старші за 12 місяців знеособлюються tool'ом `anonymize_old_records`, який «Шедулер» викликає
  щоночі (`telegram_id` → NULL у `bookings`/`event_registrations`/`bst_bookings`/`waitlist`).
- **Шифрування.** БД і бекапи шифруються на рівні диска або хостингу. Окремого шифрування колонок у MVP немає, бо
  телефони потрібні для пошуку й прив'язки психолога.

## 9. Імпорт з Google Sheets

Одноразово, F3-34: команда `dotnet psy-support.dll import --dir <папка з CSV>`. CSV вивантажуються з кожного листа
(«Файл → Завантажити → CSV»). Імпорт:

- розбирає дати в трьох форматах із таблиці (`yyyy-MM-dd`, `dd.MM.yyyy`, серійне число Sheets) і час (`HH:mm`, частка
  доби), переводить їх із `Europe/Kyiv` в UTC;
- переводить статуси (`вільний` → `open`, `активний` → `active`, `так`/`ні` → bool…);
- зв'язує `slot_key`/`session_key` з новими id і переносить лічильники й тексти `settings`;
- пропускає `log` (історію дій не переносимо);
- ідемпотентний: повторний запуск на тих самих CSV нічого не дублює (ключі зі старих id зберігаються в
  `import_map`);
- виводить звіт «скільки рядків, скільки пропущено й чому» без персональних даних.

Перехід: (1) імпорт → (2) тестовий бот на OpenBP перевіряє сценарії F3-31 → (3) у вікні без записів `setWebhook`
бойового бота перемикається з n8n на OpenBP, а n8n-сценарій вимикається (не видаляється) → (4) тиждень спостереження.
Кроки 3–4 робить власник.

## 10. Рішення, які я ухвалив сам

1. **Екрани (текст і кнопки) генерує `psy-support`, а не сценарій OpenBP.** Тексти беруться з `settings`, а кнопки несуть
   id з БД застосунку. Інакше логіка екранів розтеклася б по 40 вузлах коду, як у n8n. Альтернатива: tools повертають
   лише дані, а екрани збирає `core.code` в OpenBP. Тоді F3-31 стане втричі більшим, зате тексти будуть видні на
   канвасі.
2. **Сім tools «за розділами» для бота плюс доменні tools.** Маршрут у OpenBP має 7 гілок, а кожен розділ — один виклик.
   Альтернатива: окремий tool на кожну кнопку (~30) і двоступеневий маршрут у сценарії.
3. **Сповіщення повертаються в `notifications`, а надсилає їх OpenBP.** Так `psy-support` не потребує токена бота й
   доступу до Telegram (правило 3).
4. **Окрема Angular-адмінка в `connectors/psy-support/web`, а не сторінки в `apps/web`.** У неї інші користувачі, ролі
   й дані, а `apps/web` — конструктор процесів.
5. **Вхід психологів в адмінку — email і пароль.** Альтернатива: Telegram Login Widget, без паролів, але з
   прив'язкою до домену бота і залежністю від Telegram для входу.
6. **Окрема база на тому самому сервері Postgres.** Простіше розгортання поруч з OpenBP; на окремий сервер її можна
   перенести, змінивши рядок підключення.
7. **Знеособлення через 12 місяців.** Термін довільний, його має підтвердити власник з огляду на внутрішні правила
   компанії.

## 11. Чого цей документ не вирішує

- «Шедулер» як сценарій OpenBP — окрема задача після F3-31. Tools для нього (`due_notifications`, `generate_slots`,
  `anonymize_old_records`) входять в F3-34.
- Сповіщення про збої (`errorWorkflow` n8n) — після перенесення, через `settings.errorWorkflowId` в OpenBP.
- Публічна HTTPS-адреса для webhook (тунель або розгортання) — питання розгортання, а не цього застосунку.
