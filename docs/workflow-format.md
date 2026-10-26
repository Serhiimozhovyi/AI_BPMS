# Формат workflow JSON (formatVersion 1)

Джерело правди для движка, канвасу, REST, MCP-сервера, імпорту/експорту.
JSON Schema формату — `src/OpenBP.Shared/Schemas/workflow.schema.json`; валідатор — `WorkflowValidator`.
Та сама схема віддається через MCP-resource `openbp://schema/workflow-format`, щоб агенти писали сценарії правильно.

## Приклад

```json
{
  "formatVersion": 1,
  "name": "Заявка з Telegram → 1С",
  "settings": {
    "timezone": "Europe/Kyiv",
    "executionTimeoutSec": 300,
    "errorWorkflowId": null,
    "saveSuccessfulData": true
  },
  "nodes": [
    {
      "id": "n_webhook",
      "name": "Нова заявка",
      "type": "core.webhook",
      "typeVersion": 1,
      "position": [100, 200],
      "parameters": { "method": "POST", "path": "telegram-in" }
    },
    {
      "id": "n_llm",
      "name": "Розібрати заявку",
      "type": "ai.llm",
      "typeVersion": 1,
      "position": [360, 200],
      "parameters": {
        "provider": "anthropic",
        "model": "claude-sonnet-5-5",
        "credentialId": "cred_anthropic",
        "prompt": "Витягни замовлення з тексту: {{ $json.body.message.text }}",
        "outputSchema": {
          "type": "object",
          "required": ["customer", "lines"],
          "properties": {
            "customer": { "type": "string" },
            "lines": { "type": "array", "items": { "type": "object",
              "properties": { "sku": { "type": "string" }, "qty": { "type": "number" } } } }
          }
        }
      }
    },
    {
      "id": "n_1c",
      "name": "1С: створити замовлення",
      "type": "mcp.tool",
      "typeVersion": 1,
      "position": [620, 200],
      "parameters": {
        "serverId": "srv_1c",
        "toolName": "create_order",
        "arguments": { "customer": "{{ $json.customer }}", "lines": "{{ $json.lines }}" }
      },
      "timeoutSec": 30,
      "retry": { "maxTries": 3, "waitMs": 2000 },
      "continueOnFail": false,
      "disabled": false,
      "notes": ""
    },
    {
      "id": "n_reply",
      "name": "Відповісти в Telegram",
      "type": "mcp.tool",
      "typeVersion": 1,
      "position": [880, 200],
      "parameters": {
        "serverId": "srv_telegram",
        "toolName": "send_message",
        "arguments": {
          "chat_id": "{{ $node[\"Нова заявка\"].json.body.message.chat.id }}",
          "text": "Замовлення №{{ $json.number }} створено"
        }
      }
    }
  ],
  "connections": [
    { "from": { "node": "n_webhook", "output": 0 }, "to": { "node": "n_llm", "input": 0 } },
    { "from": { "node": "n_llm", "output": 0 }, "to": { "node": "n_1c", "input": 0 } },
    { "from": { "node": "n_1c", "output": 0 }, "to": { "node": "n_reply", "input": 0 } }
  ],
  "pinData": {
    "n_webhook": [ { "json": { "body": { "message": { "chat": { "id": 42 }, "text": "2 мішки добрива Петренку" } } } } ]
  }
}
```

## Правила

- `id` вузла — стабільний, генерується при створенні, не змінюється при перейменуванні. Вирази посилаються на **назву** (`$node["Нова заявка"]`), тому при перейменуванні канвас оновлює вирази.
- `name` унікальний у межах сценарію.
- `type` — `<пакет>.<вузол>`: `core.*`, `mcp.tool`, `mcp.poll`, `mcp.subscribe`, `mcp.waitFor`, `ai.llm`, `ai.agent`. `typeVersion` — ціле; старі версії підтримуються, поки є сценарії, що їх використовують.
- `connections` — масив ребер; один вихід → кілька ребер (розгалуження), один вхід ← кілька ребер (items об'єднуються в порядку надходження). Індекс виходу відповідає `Descriptor.Outputs`; у вузлів з `continueOnFail: true` з'являється додатковий останній вихід `error`.
- Цикли заборонені (валідатор перевіряє DAG). Ітерація — через `core.loop`.
- `pinData` використовується тільки в ручному тестовому запуску.
- Секретів у форматі немає: лише `credentialId` і `serverId`. При імпорті в інше середовище посилання перемаплюються (див. «Експорт та імпорт»).
- `$vars.key` — змінні інстансу (Налаштування → «Змінні»): спільні для всіх сценаріїв значення на зразок адреси ERP чи
  коду регіону. Не секрети — для них credentials.
- `timeoutSec` на вузлі перекриває дефолт (30 с для `mcp.tool`, 60 с для `ai.llm` і кожного запиту `ai.agent` до моделі, без ліміту для логіки).

## Обов'язкові поля

- Сценарій: `formatVersion` (= 1), `name`, `settings`, `nodes`, `connections`, `pinData` (може бути `{}`).
- `settings`: `timezone`, `executionTimeoutSec`, `errorWorkflowId` (`null`, якщо немає), `saveSuccessfulData`.
  У редакторі їх змінює діалог «Налаштування» (кнопка на панелі інструментів).
  `saveSuccessfulData: false` — у кроках успішних виконань (крім ручного тесту) лишаються лише статус і час (docs/architecture.md §10.4).
- Необов'язкові `settings` (F6-06): `concurrency`, `throttle`, `disableAfterErrors` — див. «Ліміти навантаження».
- Необов'язкове `settings.correlation` (F5-04): `{ "skipIfActive": true }` — див. «Ключ кореляції».
- Вузол: `id`, `name`, `type`, `typeVersion` (≥ 1), `position` (`[x, y]`), `parameters` (об'єкт). Необов'язкові: `timeoutSec`, `retry` (`maxTries`, `waitMs`), `continueOnFail`, `disabled`, `notes`, `business` (див. «Бізнес-атрибути»).
- Ребро: `from.node` + `from.output`, `to.node` + `to.input` (індекси від 0). Необов'язкове `business.label`.
- Сценарій: необов'язкове `business` (`lanes`, `steps`) — див. «Бізнес-атрибути».
- Сценарій: необов'язкове `stages[]` — етапи бізнес-процесу над графом, див. «Етапи».
- Сценарій: необов'язкове `notes[]` — вільні нотатки на канвасі, див. «Нотатки».
- Невідомі поля на будь-якому рівні дозволені й зберігаються при (де)серіалізації.

### Таймаут виконання і серверна межа

`executionTimeoutSec` — найбільший час виконання сценарію в секундах; `0` — власного ліміту немає. Сервер має межу
`OPENBP_MAX_EXECUTION_TIMEOUT_SEC` (типово 3600):

- збереження зі значенням, більшим за межу, відхиляється помилкою `EXECUTION_TIMEOUT_TOO_LARGE` з
  `path: "/settings/executionTimeoutSec"`;
- воркер застосовує менше з двох значень — тож сценарій з `0` чи версія, збережена до зменшення межі, теж
  не працює довше за неї;
- виконання, що перевищило час, завершується станом `timed_out` (REST, SSE, MCP і журнал виконань в UI), вузол,
  що працював, — `cancelled`, наступні вузли не запускаються.

### Сценарій помилок

`errorWorkflowId` — id іншого сценарію, поточна версія якого має вузол `core.errorTrigger` («Помилка сценарію»).
Інакше збереження відхиляється помилкою `ERROR_WORKFLOW_INVALID` з `path: "/settings/errorWorkflowId"`
(так само для неіснуючого сценарію і для посилання на себе).

Коли виконання завершилось `error` або `timed_out`, воркер ставить запуск сценарію помилок у режимі `error`;
вихід `core.errorTrigger` — один item:

```json
{
  "execution": { "id": "…", "status": "error", "startedAt": "…", "finishedAt": "…", "mode": "trigger" },
  "workflow": { "id": "…", "name": "Звіт" },
  "error": { "message": "…", "nodeId": "n_wait", "nodeName": "Пауза" }
}
```

`error.message` — той самий текст, що в журналі виконань, без значень credentials. Сценарій помилок запускається,
лише коли він активний; вимкнений, видалений чи без `core.errorTrigger` — запис у журнал воркера, виконання
завершується як звичайно. Помилка самого сценарію помилок (режим `error`) інший сценарій помилок не запускає,
тож циклу не буде. У тестовому запуску без `pinData` вузол віддає приклад такого item.

### Ліміти навантаження

Необов'язкові поля `settings` (F6-06); немає поля — немає ліміту. У редакторі — розділ «Навантаження» діалогу
«Налаштування». Ліміти діють на всі запуски, крім ручного тестового з канвасу (`manual`): тригери, розклад,
`mcp.poll`, REST/MCP `execute_workflow`, повтори і запуск як сценарію помилок.

```json
"settings": {
  "concurrency": { "max": 1, "onLimit": "queue" },
  "throttle": { "limit": 2, "periodSec": 10, "onLimit": "drop" },
  "disableAfterErrors": 5
}
```

- `concurrency.max` (1…1000) — скільки виконань сценарію може йти одночасно. `onLimit` (типово `queue`):
  `queue` — зайві запуски чекають у черзі й беруться строго в порядку надходження, щойно звільниться місце;
  `drop` — запуск понад ліміт (рахуються `queued`, `running`, `cancelling`) одразу записується як `cancelled`
  з поясненням у `error`, job не ставиться.
- `throttle.limit` (1…100000) за `throttle.periodSec` (1…86400) — скільки запусків дозволено в одному вікні.
  Вікна фіксовані й відлічуються від початку епохи Unix (для `periodSec: 60` — щохвилини о `:00`).
  `queue` — запуск понад ліміт переноситься на початок найближчого вікна з вільним місцем (поле
  `executions.scheduled_at`, стан `queued`); `drop` — записується як `cancelled` з поясненням.
- `disableAfterErrors` (0…1000; `0` — вимкнено) — запобіжник: після стількох помилкових (`error`, `timed_out`)
  виконань режиму `trigger` поспіль сценарій деактивується (webhook'и знімаються, розклад нічого не запускає).
  Успіх обриває серію; ручні запуски, повтори й скасовані виконання не рахуються; лічба починається заново після
  кожної активації.

Інші сценарії лімітами одного сценарію не блокуються.

### Очікування (`core.wait`)

- Версія 1 (`typeVersion: 1`, збережені сценарії): `amount` у `unit` (`milliseconds`…`hours`), до 1 год; пауза тримає
  воркер.
- Версія 2 (`typeVersion: 2`, F5-02): `mode: "duration"` — `amount` у `unit`
  (`seconds`, `minutes`, `hours`, `days`); `mode: "until"` — `until`, дата ISO 8601 (можна вираз), без зсуву — у
  `settings.timezone`. Найдовше — 90 днів; минула дата — items ідуть далі одразу. До 30 с вузол чекає в пам'яті, довше —
  виконання переходить у стан `waiting` до `resumeAt`, воркер вільний, а в призначений час виконання прокидається й
  продовжує з наступного вузла (docs/architecture.md §10.4 «Довге очікування»). Скасувати `waiting` можна з журналу
  виконань, REST або MCP `cancel_execution`.

```json
{ "id": "n_wait", "name": "Чекати 2 дні", "type": "core.wait", "typeVersion": 2, "position": [400, 0],
  "parameters": { "mode": "duration", "amount": 2, "unit": "days" } }
```

- Версія 3 (`typeVersion: 3`, палітра пропонує її для нових вузлів, F6-01): режими `duration` і `until` як у версії 2 плюс
  пробудження зовні — `mode: "webhook"` (чекати `POST` на адресу пробудження) і `mode: "form"` (чекати, доки людина
  надішле форму на сторінці за адресою пробудження, F6-03, — див. «Форми» нижче). Виходи — `main` і `timeout`: дані, що розбудили вузол, ідуть на `main`
  (об'єкт — один item, масив об'єктів — item на елемент, інше — `{ "body": … }`), а коли минув `maxWaitSec` (секунди,
  типово і не більше 90 днів) без відповіді — вхідні items ідуть на `timeout`. Режими за часом вихід `timeout` не
  використовують. Поки вузол чекає, виконання у стані `waiting`, воркер вільний; воркер може впасти й піднятися — сигнал
  не губиться.
- Адреса пробудження — `POST /webhook/resume/{token}` (режим `webhook`) або сторінка `/form/resume/{token}` (режим
  `form`) з одноразовим випадковим токеном на пару (виконання, вузол).
  Воркер видає токени на старті виконання, тож адресу можна вставити в лист чи повідомлення **до** вузла очікування:
  `$execution.resumeUrl` — коли такий вузол у сценарії один, `$execution.resumeUrls["Назва вузла"]` — за назвою вузла (з
  кількома вузлами `resumeUrl` не визначений). Адреса будується з `OPENBP_PUBLIC_URL` (docs/admin/env.md; змінна потрібна й
  воркеру). Тіло запиту (JSON, форма або текст) стає виходом вузла. Відповідь `202 { executionId }`; невідомий токен — 404;
  повторний запит, завершене чи скасоване виконання, інший вузол, що чекає, або строк, що вже вийшов — 409
  (`RESUME_TOKEN_USED`, `EXECUTION_NOT_WAITING`, `NODE_NOT_WAITING`). Токен режиму `form` через `/webhook/resume` не
  приймається (404): дані форми проходять перевірку за її схемою. Шляхи тригерів `core.webhook` і `core.formTrigger` не
  можуть починатися з `resume/`.

```json
{ "id": "n_mail", "name": "Лист", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
  "parameters": { "serverId": "…", "toolName": "send_mail", "arguments": { "text": "Підтвердьте: POST {{ $execution.resumeUrl }}" } } },
{ "id": "n_wait", "name": "Відповідь", "type": "core.wait", "typeVersion": 3, "position": [400, 0],
  "parameters": { "mode": "webhook", "maxWaitSec": 259200 } }
```

У `openbp run` (CLI) HTTP-сервера немає: очікування `webhook`/`form` перемотується до `maxWaitSec` і йде виходом `timeout`.

### Форми (`core.formTrigger`, `core.wait` у режимі `form`)

Публічна сторінка з полями за JSON Schema (F6-03): людина заповнює форму в браузері, сценарій отримує дані.

```json
{ "id": "n_form", "name": "Заява", "type": "core.formTrigger", "typeVersion": 1, "position": [0, 0],
  "parameters": {
    "path": "hr/leave", "title": "Заява на відпустку", "description": "Заповніть за тиждень до відпустки.",
    "schema": { "type": "object", "required": ["name", "days"], "properties": {
      "name": { "type": "string", "title": "ПІБ", "minLength": 2 },
      "days": { "type": "integer", "title": "Днів", "minimum": 1, "maximum": 28 },
      "kind": { "type": "string", "title": "Вид", "enum": ["annual", "unpaid"] },
      "scan": { "type": "string", "format": "binary", "title": "Скан заяви" } } },
    "submitLabel": "Надіслати заяву", "auth": "none", "onSubmit": { "message": "Дякуємо, заяву прийнято" } } }
```

- `core.formTrigger` — тригер: при активації шлях `path` реєструється (таблиця `webhooks`, `kind = form`), сторінка —
  `/form/{path}` (без меню застосунку). Шлях — сталий рядок як у `core.webhook`, унікальний серед активних форм
  (`WEBHOOK_PATH_CONFLICT`); той самий шлях у `core.webhook` — інша адреса, конфлікту немає. `schema` має бути
  коректною JSON Schema з `type: object` — інакше активація дає `PARAMETER_INVALID` на `/parameters/schema`.
- Поля (`schema.properties`): `string` (формати `email`, `date`, `date-time`, `uri`, `textarea` — багаторядкове, `binary` —
  файл), `number`, `integer`, `boolean` — прапорець, `enum` або `oneOf` з `const`/`title` — список, масив з `items.enum` —
  кілька значень. `title` — підпис, `description` — підказка, `required`, `minLength`/`maxLength`, `minimum`/`maximum`,
  `pattern` перевіряються і на сторінці, і на сервері. Порожні поля вважаються незаповненими.
- `auth`: `none` (типово) — будь-хто за посиланням; `login` — лише користувач OpenBP (без сесії сторінка веде на вхід і
  повертає на форму); `token` — посилання `/form/{path}?token=…`, значення токена — у credential типу `form.token`
  (`credentialId`); без токена чи з чужим — 401.
- `onSubmit`: `message` — текст після надсилання (типово «Дякуємо! Форму надіслано.»), `redirectUrl` — перейти на адресу
  `http(s)://…`.
- Item тригера: `{ "fields": { … }, "submittedAt": "ISO 8601", "submittedBy"?: { "id", "email" } }` (`submittedBy` — коли
  надсилав користувач OpenBP). Файл з поля `format: binary` — у `binary` під назвою поля (`BinaryRef` зі сховищем
  `postgres`), у `fields` — назва файлу; один файл на поле. Ліміти сервера — розмір тіла (`OPENBP_FORM_MAX_BODY_MB`,
  типово 10) і кількість файлів (`OPENBP_FORM_MAX_FILES`, типово 10), docs/admin/env.md.
- `core.wait` v3 з `mode: "form"` показує ту саму сторінку за `$execution.resumeUrl` (`/form/resume/{token}`); що на ній —
  параметр `form`: `{ "title", "description", "schema", "submitLabel", "message" }` (вирази обчислюються, коли виконання
  доходить до вузла; без `title` — назва вузла, без `schema` — лише кнопка). Надіслана форма будить виконання: на вихід
  `main` іде item з тими самими `fields`, `submittedAt`, `submittedBy?` і `binary`. Сторінка вже надісланої форми каже
  «вже надіслано», неактуальної — «процес завершено або строк вийшов».

```json
{ "id": "n_wait", "name": "Погодження", "type": "core.wait", "typeVersion": 3, "position": [400, 0],
  "parameters": { "mode": "form", "maxWaitSec": 259200, "form": {
    "title": "Погодьте заявку {{ $json.id }}", "submitLabel": "Погодити", "message": "Рішення записано",
    "schema": { "type": "object", "required": ["decision"], "properties": {
      "decision": { "type": "string", "title": "Рішення", "oneOf": [ { "const": "approve", "title": "Погодити" }, { "const": "reject", "title": "Відхилити" } ] },
      "comment": { "type": "string", "format": "textarea", "title": "Коментар" } } } } } }
```

Чекати не часу, а події в зовнішній системі (задачу закрили, оплата надійшла) — `mcp.waitFor` (F5-03): перевіряє умову
над відповіддю tool'а конектора з інтервалом, між перевірками виконання теж у `waiting`; виходи `done` і `timeout`
(docs/connectors.md §3.1). Різниця з `core.wait` у режимі `webhook`: там умову перевіряє сам сценарій через tool, тут
зовнішня система чи людина викликає OpenBP.

### Погодження (`core.approval`)

Людська задача з рішенням (F6-02). Вузол створює задачу (таблиця `tasks`), чекає, не тримаючи воркер (як `core.wait` у
режимі `webhook`), і продовжує виконання виходом прийнятого рішення.

- Параметри: `title` (обов'язковий), `description`, `decisions: [{ key, label }]` (до 8; типово `approve` «Погодити» і
  `reject` «Відхилити»), `fields` — JSON Schema даних до рішення (коментар тощо; ті самі типи полів, що в
  `core.formTrigger`, без файлів), `assignee` — рівно одне з `userId` (користувач OpenBP), `role` (`viewer` | `editor` |
  `admin` — усі з цією роллю або вищою) чи `expression` (email користувача OpenBP або зовнішній id, напр. Telegram id;
  можна вираз), `maxWaitSec` (до 90 днів, типово 90 днів), `escalateTo` — виконавець тієї ж форми. Вирази в
  `title`/`description`/`fields`/`assignee` обчислюються, коли виконання доходить до вузла.
- Виходи фіксовані: `timeout` (індекс 0), далі `decision0…decision7` — рішення `i` веде на вихід `i + 1`, тож індекси
  не зсуваються, коли рішення додають чи прибирають; канвас показує `timeout` і по порту на рішення.
- Рішення: item `{ "decision": "approve", "data": { …за fields }, "completedBy": { "id", "email" } | null, "completedAt" }`.
- Строк `maxWaitSec` вийшов без рішення: з `escalateTo` задача один раз переходить до нового виконавця ще на такий самий
  строк (позначка «ескалація»), без нього або після ескалації — вхідні items ідуть на вихід `timeout`, задача стає
  `expired`.
- Де людина приймає рішення: сторінка `/tasks` (мої задачі, усі — для admin) і картка `/tasks/{id}`; посилання на картку
  для листа чи повідомлення — `$execution.taskUrl` (один такий вузол у сценарії) або `$execution.taskUrls["Назва вузла"]`,
  відоме ще до вузла; без входу сторінка веде на `/login` і повертає на картку. Кнопки в Telegram робить сам сценарій
  через конектор: `callback_data` — токен з кінця `taskUrl`, обробник викликає `POST /api/v1/tasks/{token}/complete`
  з API-ключем ролі editor (зовнішню задачу закриває editor і вище). Те саме — MCP-tools `list_tasks`, `get_task`,
  `complete_task` (docs/mcp-server.md).
- Права: admin бачить і закриває всі задачі; решта — свої (виконавець — вони, їхній email або їхня роль). Повторне
  рішення — 409 `TASK_CLOSED`.
- Завершення або скасування виконання, як і скасування гілки `core.merge` `any`/`count`, закриває відкриту задачу
  статусом `cancelled` (ADR-004 §5).

```json
{ "id": "n_appr", "name": "Погодження знижки", "type": "core.approval", "typeVersion": 1, "position": [400, 0],
  "parameters": { "title": "Знижка {{ $json.discount }}% для {{ $json.client }}", "assignee": { "expression": "{{ $json.managerEmail }}" },
    "maxWaitSec": 86400, "escalateTo": { "role": "admin" },
    "fields": { "type": "object", "properties": { "comment": { "type": "string", "format": "textarea", "title": "Коментар" } } } } }
```

Відмінність від `mcp.waitFor`: там умову перевіряє сценарій через tool зовнішньої системи (задачу закрили в Кіті), тут
рішення приймає людина в OpenBP або за посиланням; у сценарії можна поєднувати обидва.

### Підсценарії (`core.executeWorkflow`, `core.workflowTrigger`)

Спільну частину процесу (перевірити склад, створити задачу в CRM) можна винести в окремий сценарій і викликати з
інших (F6-04).

```json
{ "id": "n_in", "name": "Вхід", "type": "core.workflowTrigger", "typeVersion": 1, "position": [0, 0],
  "parameters": { "inputSchema": { "type": "object", "required": ["dealId"] } } }
```

```json
{ "id": "n_call", "name": "Перевірити склад", "type": "core.executeWorkflow", "typeVersion": 1, "position": [400, 0],
  "parameters": { "workflowId": "0199…", "mode": "wait", "items": "{{ $json.lines }}" } }
```

- `core.workflowTrigger` — тригер підсценарію. `inputSchema` (необов'язково) — JSON Schema для `json` кожного вхідного
  item'а. Сценарій, єдиний тригер якого — цей, **не активується** (`WORKFLOW_TRIGGER_STANDALONE`): викликати його можна
  й неактивним, а публікувати — як звичайно. Для тесту з канвасу — `pinData` на цьому вузлі; без неї він віддає один
  порожній item.
- `core.executeWorkflow` — один запуск вузла дає одне дочірнє виконання (`mode = sub`) **опублікованої** (без публікації —
  поточної) версії сценарію `workflowId`; items вузла стають виходом його `core.workflowTrigger` (сам тригер не
  виконується). `items` — вираз для кожного вхідного item'а: масив — по item'у на елемент, об'єкт — один item, інше
  значення — `{ "value": … }`; без виразу — усі вхідні items як є. `workflowId` і `mode` беруться з першого item'а.
- `mode: "wait"` (типово) — вихід вузла = вихід дочірнього виконання: items останнього успішного вузла (порт 0, а якщо він
  порожній — перший непорожній). До 10 с вузол чекає в пам'яті, далі виконання переходить у `waiting`, воркер вільний;
  щойно дочірнє завершилось, батьківське прокидається (запасна перевірка — з інтервалом 5 с … 1 хв). Помилка,
  скасування чи таймаут дочірнього — помилка вузла з текстом і id дочірнього виконання: працюють `retry` і
  `continueOnFail`.
- `mode: "fireAndForget"` — вузол одразу віддає один item `{ "executionId": "…" }` і не чекає.
- Items не за `inputSchema`, сценарій без увімкненого `core.workflowTrigger`, сценарій іншого проєкту, глибина вкладення
  більша за 8 (виконання верхнього рівня — 0) — помилка вузла, дочірнє виконання не створюється.
- Підсценарій зберігає дані кроків навіть із `saveSuccessfulData: false` — його вихід забирає батьківський вузол.
- Валідація (при збереженні, публікації, активації): для літерального `workflowId` — сценарій існує, спільний або того
  самого проєкту і має увімкнений `core.workflowTrigger` (`SUBWORKFLOW_INVALID`); виклики між сценаріями (за версіями,
  які виконає запуск) не утворюють циклу A → B → A, зокрема виклик самого себе (`SUBWORKFLOW_CYCLE`). `workflowId`-вираз
  перевіряє лише воркер під час виконання.
- Журнал виконань: у деталях батьківського — список запущених підсценаріїв (вузол, стан, посилання), у деталях
  дочірнього — посилання на батьківське; REST `GET /executions/{id}` і MCP `get_execution` віддають `children[]`, а
  `summary` дочірнього — `parentExecutionId`, `parentNodeId`.

### Злиття гілок (`core.merge`)

- Версія 1 (`typeVersion: 1`, збережені сценарії): два входи, вузол виконується, коли items є на обох; `mode: "append"`
  (спершу вхід 1, потім вхід 2) або `"combineByPosition"` (попарно злити `json` items з однаковим індексом).
- Версія 2 (`typeVersion: 2`, палітра пропонує її для нових вузлів, F7-06, ADR-004 §5): входи `input1…input8`, канвас
  показує `branches` (2…8, типово 2). `mode`:
  - `all` (типово) — чекати всі під'єднані гілки; виконується, коли items є на кожному під'єднаному вході;
  - `any` — пропустити першу гілку, що дійшла з items, решту скасувати;
  - `count` + `count` (1…8, не більше під'єднаних входів) — пропустити перші `count` гілок, решту скасувати.

  Скасована гілка: її вузли, що ще не стартували або чекають (`core.wait`, `mcp.waitFor` у `waiting`), отримують крок
  `cancelled` (`error.type: "BRANCH_CANCELLED"`), job пробудження не створюється, подальші вузли гілки не стартують.
  Вузол гілки, що має ще й інший вихід (сповіщення поруч), не скасовується. Гілок з items менше, ніж треба, коли всі
  завершились, — крок `skipped`, як у v1. `combine`: `append` (items гілок, що пройшли, одна за одною в порядку входів)
  або `combineByPosition` (попарне злиття всіх таких входів, кількості items мають збігатися). Бізнес-вигляд підписує
  ребра через злиття «будь-яка з гілок» (`any`) або «N з M» (`count`).

```json
{ "id": "n_first", "name": "Перша відповідь", "type": "core.merge", "typeVersion": 2, "position": [600, 0],
  "parameters": { "mode": "any", "branches": 2 } }
```

Завершення або скасування виконання закриває все незавершене: вузли в `waiting` отримують крок `cancelled`
(`error.type: "WAIT_CLOSED"`), job пробудження видаляється (docs/architecture.md §2, §10.4).

### Ключ кореляції

Ключ кореляції (F5-04) — бізнес-ідентифікатор запуску (id угоди, номер заявки), щоб знаходити всі виконання по
одному об'єкту й не запускати дубль, поки попередній ще йде.

- Тригери `core.webhook`, `mcp.poll` і `mcp.subscribe` мають необов'язковий параметр `correlationKey` — вираз над item'ом тригера,
  напр. `{{ $json.body.deal?.id }}` або `{{ $json.Id }}`. Рядок і число стають текстом, об'єкт — JSON, порожнє значення —
  «без ключа»; ключ обрізається пробілами й до 200 символів.
- Webhook: помилка виразу — відповідь 400. `mcp.poll` і `mcp.subscribe` з ключем запускають **окреме виконання на кожен новий запис**
  (без ключа — одне виконання з усіма записами); помилка виразу для запису — попередження в журналі воркера, запис
  запускається без ключа.
- REST `POST /workflows/{id}/executions` і MCP `run_workflow` приймають `correlationKey` (довше за 200 символів — 400).
  Повтор і «Продовжити з вузла» переносять ключ оригіналу.
- Фільтр: `GET /executions?correlationKey=` (точний збіг), MCP `list_executions { correlationKey }`, поле й колонка
  «Ключ» на сторінці «Виконання» (клік по ключу показує всі виконання з ним).

```json
"settings": { "correlation": { "skipIfActive": true } }
```

- `skipIfActive` — якщо в сценарію вже є активне виконання (`queued`, `running`, `waiting`, `cancelling`) з тим самим ключем,
  новий запуск одразу записується як `cancelled` з поясненням у `error` (id активного виконання), job не ставиться;
  webhook відповідає 202 з id цього запису. Діє на всі режими, зокрема ручний запуск із ключем. Запуск без ключа не
  перевіряється. У редакторі — перемикач у діалозі «Налаштування».

### Відповідь webhook-у й дедуплікація (`core.webhook`, `core.respondToWebhook`)

Що webhook відповідає викликачу і як він відсікає повтори того самого запиту (F6-05).

```json
{ "id": "n_hook", "name": "Заявка", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
  "parameters": { "method": "POST", "path": "orders", "respond": "fromNode", "responseTimeoutSec": 20,
                  "idempotencyKey": "{{ $json.body.orderId }}", "dedupeWindowSec": 3600 } }
```

```json
{ "id": "n_reply", "name": "Відповідь", "type": "core.respondToWebhook", "typeVersion": 1, "position": [600, 0],
  "parameters": { "statusCode": 201, "headers": { "X-Order": "{{ $json.id }}" }, "body": { "id": "{{ $json.id }}" } } }
```

- `respond`:
  - `immediately` (типово, як було) — одразу 202 `{ "executionId": "…" }`;
  - `whenFinished` — Api чекає завершення: успіх — 200 JSON з виходом останнього успішного вузла (порт 0, а якщо він
    порожній — перший непорожній; один item — його `json`, кілька — масив); `error`/`timed_out` — 500
    `WEBHOOK_EXECUTION_FAILED`, `cancelled` — 409 `WEBHOOK_EXECUTION_CANCELLED`. Працює й із `saveSuccessfulData: false`;
  - `fromNode` — відповідає вузол `core.respondToWebhook`; виконання після нього триває. Виконання завершилось, а
    вузол не відповів (гілка не дійшла) — 500 `WEBHOOK_NO_RESPONSE`.
- `responseTimeoutSec` (1…300, типово 30) — скільки Api чекає для `whenFinished`/`fromNode`; не дочекались — 504
  `WEBHOOK_RESPONSE_TIMEOUT` (`application/problem+json` з `executionId` і `executionStatus`), виконання триває.
- `core.respondToWebhook`: `statusCode` (100…599, типово 200); `headers` (ім'я → рядок; `Content-Length`,
  `Transfer-Encoding`, `Connection` ігноруються); `body` — рядок стає `text/plain`, інше значення — JSON, без `body` —
  `json` першого item'а; `binaryKey` — замість тіла файл із `binary` першого item'а з його MIME-типом і
  `Content-Disposition: attachment`. Тіло — до 10 МБ. Свій `Content-Type` у `headers` має перевагу. Відповідає лише
  перший такий вузол у виконанні; items проходять на вихід без змін. Без воркера (`openbp run`) — помилка
  вузла.
- Дедуплікація: ключ — `idempotencyKey` (вираз над item'ом тригера, як `correlationKey`; помилка виразу — 400), без
  нього — заголовок `Idempotency-Key`; порожнє значення — без дедуплікації; обрізається до 200 символів. Повтор
  ключа того самого webhook-вузла у вікні `dedupeWindowSec` (1…2592000, типово 86400) нового виконання не створює:
  відповідь, що вже відома, повертається така сама з заголовком `Idempotent-Replayed: true`; якщо перше виконання ще
  не відповіло — повтор чекає на нього. 504 не запам'ятовується. Після вікна той самий ключ запускає нове виконання.
- Не плутати з `correlationKey` (вище): той не дає запустити друге **активне** виконання з тим самим бізнес-ключем,
  `idempotencyKey` відсікає повторний **HTTP-запит** у вікні часу незалежно від стану виконання. У вузлі вони поруч.
- Валідація: `respond: "fromNode"` без увімкненого `core.respondToWebhook`, досяжного з цього webhook-а, —
  `WEBHOOK_RESPOND_NODE_MISSING`.

### Таблиці даних (`core.dataTable`)

Вузол «Таблиця даних» читає й пише таблиці зі сторінки «Таблиці» (`/tables`, F6-11): довідники, черги, стан між
запусками. Таблиця має колонки `{ name, type: string|number|boolean|date|json, key? }`; ключова — не більше однієї
(рядок або число), за нею працюють `get`, `upsert`, `update`, `delete`. Без ключової колонки ключ рядка — згенерований id.

```json
{
  "id": "n_save", "name": "Зберегти клієнта", "type": "core.dataTable", "typeVersion": 1, "position": [400, 0],
  "parameters": {
    "table": "Клієнти",
    "operation": "upsert",
    "values": { "email": "{{ $json.email }}", "visits": "{{ $json.visits }}" }
  }
}
```

- `table` — назва (без урахування регістру) або id; `operation` — `get` | `list` | `insert` | `upsert` | `update` | `delete`.
- `key` — значення ключа для `get`, `update`, `delete`; для `insert`/`upsert` ключ береться з `values`.
- `values` — значення колонок; змінюються лише передані (`update`, `upsert` по наявному рядку). Типи приводяться:
  число з рядка `"3"`, булеве з `"true"`, дата — в ISO UTC; невідома колонка чи невідповідний тип — помилка вузла.
- `where` — для `list`: `[{ column, operator, value }]` через AND; оператори `eq`, `ne`, `gt`, `gte`, `lt`, `lte`,
  `contains` (підрядок без урахування регістру; для json — у будь-якому значенні всередині), `isEmpty`, `isNotEmpty`.
  Колонки json — лише `contains`/`isEmpty`/`isNotEmpty`. `limit` — 1…1000, типово 100.
- Вузол виконується для кожного вхідного item; вихід — item на кожен рядок результату: значення колонок плюс `_key`.
  `get` без такого рядка — нуль items; `update` неіснуючого — помилка; `delete` — `{ _key, deleted }`.
- `upsert` ідемпотентний: паралельні запуски з одним ключем дають один рядок.
- Ліміти інстансу: рядків у таблиці — `OPENBP_DATA_TABLE_MAX_ROWS` (типово 100 000), розмір рядка —
  `OPENBP_DATA_TABLE_MAX_ROW_BYTES` (типово 65 536 байт); перевищення — помилка вузла `DATA_TABLE_ROW_LIMIT` / `DATA_TABLE_ROW_TOO_LARGE`.

### Таблиця рішень (`core.decisionTable`)

Вузол «Таблиця рішень» (F6-19) підбирає значення полів за правилами: рядки — правила, колонки — умови на входи й
значення виходів. Панель вузла редагує правила таблицею.

```json
{
  "id": "n_discount", "name": "Знижка", "type": "core.decisionTable", "typeVersion": 1, "position": [400, 0],
  "parameters": {
    "inputs": [
      { "name": "segment", "expression": "{{ $json.segment }}" },
      { "name": "total", "expression": "{{ $json.total }}" }
    ],
    "outputs": [{ "name": "discount" }, { "name": "note" }],
    "rules": [
      { "when": [{ "operator": "equals", "right": "vip" }, { "operator": "gte", "right": 1000 }], "then": [15, "VIP великий"] },
      { "when": [{ "operator": "equals", "right": "vip" }, "-"], "then": [10, "VIP"] },
      { "when": ["-", { "operator": "gte", "right": 5000 }], "then": [5, "{{ 'від ' + $json.total }}"] }
    ],
    "hitPolicy": "first"
  }
}
```

- `inputs` — значення, які перевіряють правила (зазвичай вирази); `outputs` — поля item'а, куди записуються значення.
- `rules[].when` — по комірці на кожен вхід у порядку `inputs`: `"-"` — будь-яке значення, інакше `{ operator, right }`
  з операторами `core.if` (`equals`, `gt`, `contains`, `isEmpty`…; ліве значення — вхід). Правило збігається, коли
  виконані всі його комірки. `rules[].then` — по значенню на кожен вихід (можна вирази).
- `hitPolicy`: `first` (типово) — перше правило, що збіглося, один item; `all` — окремий item на кожне правило, що
  збіглося, у порядку правил; `collect` — один item, у полях виходів масиви значень усіх збігів.
- Виходи вузла: `main` (індекс 0) — items з дописаними полями; `noMatch` (індекс 1) — item без змін, якщо жодне
  правило не збіглося.
- Помилки — з номером правила: кількість комірок не дорівнює кількості входів/виходів, несумісні типи в умові
  (`Правило 2, вхід «total»: …`), вираз, що не обчислився (`Правило 3: Вираз у параметрі «rules[2].then[1]» …`).
  Вирази таблиці обчислюються для кожного item'а повністю, тож помилка у виразі правила видна, навіть якщо правило
  не збіглося.

## Бізнес-атрибути (F3-67)

Необов'язкова розмітка для бізнес-вигляду сценарію ([business-view.md](business-view.md)): доріжки учасників, назви кроків
людською мовою, дії людей у зовнішніх системах і підписи стрілок. Рушій ці поля **ігнорує**; без них бізнес-вигляд
будується за правилами за замовчуванням. Поля додано до `formatVersion` 1 як необов'язкові: документи без них лишаються
валідними і не змінюються при збереженні. REST, `/mcp` (`get_workflow`/`update_workflow`) і експорт/імпорт переносять
їх без втрат, тож ШІ-агент може розмітити сценарій сам. Повний приклад — `examples/kit-approval-business.json`
(сценарій погодження в Кіт 2; він не входить у CLI-прогони, бо вузли Кіта ще без конектора).

```json
{
  "business": {
    "lanes": [
      { "id": "kit", "name": "Кіт 2", "description": "люди у веб-формах" },
      { "id": "openbp", "name": "OpenBP", "description": "сценарій" }
    ],
    "steps": [
      { "id": "s_approve", "title": "Погоджувач тисне «Погодити» у формі", "subtitle": "статус задачі", "lane": "kit",
        "links": [ { "node": "n_assign", "direction": "out", "label": "ставить задачу в Кіті" },
                   { "node": "n_read", "direction": "in", "label": "читає статус" } ] }
    ]
  },
  "nodes": [
    { "id": "n_assign", "name": "Assign approver", "type": "mcp.tool", "...": "...",
      "business": { "title": "Призначити погоджувача", "subtitle": "kit-crm", "lane": "openbp" } }
  ],
  "connections": [
    { "from": { "node": "n_decision", "output": 2 }, "to": { "node": "n_mail", "input": 0 },
      "business": { "label": "2 дні без рішення" } }
  ]
}
```

| Рівень | Поле | Значення |
|---|---|---|
| сценарій | `business.lanes[]` | доріжки в порядку показу: `id` (унікальний), `name`, `description?` (підпис під назвою). Немає — доріжка за типом вузла |
| сценарій | `business.steps[]` | дії людей у зовнішніх системах, яких немає в графі: `id` (унікальний), `title`, `subtitle?`, `lane` (id з `lanes`), `links[]` — хоча б один зв'язок `{ node, direction, label? }`; `direction: "in"` — вузол читає результат кроку (пунктирна стрілка крок → вузол), `"out"` — вузол ставить людині задачу (суцільна стрілка вузол → крок) |
| вузол | `business` | `title?` (замість `name`), `subtitle?`, `lane?` (id з `lanes`; немає — за типом вузла), `hidden?` (`true` — не показувати; `false` — показувати `core.set`/`core.code`/`core.merge`, який інакше згортається) |
| ребро | `business.label?` | підпис стрілки; немає — назва виходу (`так`/`ні`, `timeout`, назва правила `switch`) |

Валідатор перевіряє (коди помилок — у «Валідації при збереженні»): унікальність `id` доріжок і кроків; `lane` вузла
чи кроку є в `business.lanes` (без `lanes` будь-яке посилання на доріжку — помилка, бо доріжки за замовчуванням id не
мають); `links[].node` — існуючий вузол; у кроку є хоча б один зв'язок. Невідомі поля всередині `business` зберігаються,
як і будь-де у форматі. Редактор при видаленні вузла прибирає зв'язки кроків на нього; крок, що лишився без зв'язків,
видаляється.

## Етапи (F7-01, ADR-004)

Необов'язковий шар над графом для бізнес-процесів ([ADR-004](adr/ADR-004-stages.md)): етап групує вузли того самого
графа, має виконавця, строк і правила переходу. **Одиниця виконання лишається вузол**: без `stages` рушій працює як
раніше, боти й шедулери етапів не мають. Стан етапу рушій обчислює з виконання (F7-02), правила перевіряє на ребрах, що
виходять з етапу (F7-03), контракт «стан · дії · умови» — F7-05. Поля додано до `formatVersion` 1 як необов'язкові; REST,
`/mcp` і експорт/імпорт переносять їх без втрат. Повний приклад — `examples/kit-approval-stages.json` (не входить у
CLI-прогони, бо вузли Кіта без `serverId`).

```json
{
  "stages": [
    {
      "id": "st_approval", "name": "Погодження",
      "nodes": ["n_wait_decision", "n_read_decision", "n_decision"],
      "owner": { "lane": "kit" },
      "due": { "amount": 2, "unit": "businessDays" },
      "transitions": [
        { "to": "st_execution", "rules": [
          { "id": "r_comment", "condition": "{{ $json.Comment != '' }}", "mode": "warn",
            "message": "Погоджувач не залишив коментар до рішення." } ] }
      ]
    },
    { "id": "st_execution", "name": "Виконання", "nodes": ["n_task_executor", "n_wait_data", "n_read_fields", "n_check"],
      "owner": { "lane": "kit" }, "due": { "amount": 3, "unit": "businessDays" } }
  ]
}
```

| Поле | Значення |
|---|---|
| `id`, `name` | `id` унікальний у сценарії; на нього посилаються `transitions[].to` |
| `nodes[]` | id вузлів етапу (≥ 1). Кожен вузол — щонайбільше в одному етапі; вузли без етапу дозволені (сповіщення, технічні кроки). Вузли етапу мають бути зв'язані між собою ребрами (без урахування напряму); шлях може проходити через `core.set`/`core.code`/`core.merge` і вимкнені вузли, що не належать жодному етапу |
| `owner?` | хто виконує: `lane` (id з `business.lanes`), `role` або `expression` (вираз, що дає email чи id) |
| `due?` | строк від входу в етап: `amount` (≥ 1) + `unit` (`minutes`, `hours`, `days`, `businessDays` — пн–пт за `settings.timezone`) або `until` (вираз дати) |
| `transitions[]?` | правила на переходах: `to` — id етапу-призначення, без `to` — усі виходи з етапу; `rules[]` — `{ id, condition, mode?, message? }`: `id` унікальний у межах етапу, `condition` — вираз над items переходу (`true` — виконане), `mode` — `block` (перехід чекає виправлення або відхилення з причиною) чи `warn` (типово: проходить, порушення фіксується), `message` — текст для людини |

Перехід — ребро з вузла етапу у вузол іншого етапу або без етапу; ребра між вузлами одного етапу внутрішні.
Правила перевіряються перед запуском вузла-цілі (F7-03): `condition` обчислюється на кожному item переходу (`$json` —
item), правило виконане, лише якщо всі дають `true`; помилка виразу — невиконане. Невиконане `block`-правило
зупиняє перехід: виконання переходить у `waiting` (крок вузла-цілі з `waitState.reason = STAGE_RULE_BLOCKED`), правило
перевіряється знову щогодини, а людина чи агент може відхилити його з причиною (`POST /executions/{id}/overrides`,
MCP `override_rule`). Кожне невиконане правило фіксується як порушення (`violations`) з items переходу. Невідомі
поля всередині `stages` зберігаються, як і будь-де у форматі.

## Нотатки (F3-62)

Необов'язкові вільні нотатки (sticky notes) на канвасі: пояснення для колег поруч із вузлами, список «зробити»,
посилання на регламент. Нотатка не пов'язана з вузлами й ребрами: **рушій її ігнорує**, валідатор перевіряє лише
схему (жодних правил про положення чи текст). Поле додано до `formatVersion` 1 як необов'язкове: документи без нього
лишаються валідними і не змінюються при збереженні; REST, `/mcp` (`get_workflow`/`update_workflow`) і експорт/імпорт
переносять `notes` без втрат, тож ШІ-агент може лишити нотатку на канвасі сам.

```json
{
  "notes": [
    { "id": "note_in", "text": "## Вхід\n\nЗаявки з **Telegram**; пік — уранці.", "position": [80, 40],
      "size": [260, 140], "color": "yellow" },
    { "id": "note_todo", "text": "- [ ] узгодити ліміт 1С", "position": [620, 380] }
  ]
}
```

| Поле | Значення |
|---|---|
| `id` | унікальний у сценарії; редактор генерує при створенні (`note_` + hex) |
| `text` | Markdown (заголовки, **жирний**, *курсив*, списки, `код`, посилання); порожній рядок дозволений |
| `position` | `[x, y]` лівого верхнього кута на канвасі — ті самі координати, що й `position` вузла |
| `size?` | `[ширина, висота]` на канвасі; немає — розмір редактора за замовчуванням (240 × 120) |
| `color?` | Highlight-колір AC-Kit: `yellow` (типово), `green`, `blue`, `teal`, `orange`, `red`, `violet`; редактор малює 15 %-відтінок із рамкою кольору |

У редакторі: `Shift+S` або ПКМ на порожньому місці → «Додати нотатку» — нова нотатка там, де вказівник (з клавіатури — в
центрі екрана) одразу в режимі редагування; подвійний клік — редагувати (`Ctrl+Enter` чи клік поза нотаткою зберігає,
`Escape` скасовує); колір — з палітри над виділеною нотаткою; розмір — за кут. Невідомі поля всередині `notes`
зберігаються, як і будь-де у форматі.

## Item і BinaryRef

Item — `{ "json": { … }, "binary"?: { "<ключ>": BinaryRef } }`. Байти файлу в item не потрапляють, лише посилання на файлове сховище:

| Поле | Тип | Обов'язкове | Опис |
|---|---|---|---|
| `id` | string | так | ключ файлу у сховищі |
| `mimeType` | string | так | напр. `application/pdf` |
| `fileName` | string | ні | оригінальна назва |
| `fileExtension` | string | ні | розширення без крапки |
| `fileSize` | integer | ні | розмір у байтах |
| `storage` | `local` \| `s3` | ні | провайдер сховища |
| `directory` | string | ні | каталог / префікс у сховищі |
| `sha256` | string | ні | хеш вмісту, hex у нижньому регістрі |

## Валідація при збереженні (однакова для UI, REST і MCP `validate_workflow`)

1. JSON відповідає схемі формату.
2. Усі `connections` посилаються на існуючі вузли й порти.
3. Граф без циклів.
4. Активний сценарій має ≥ 1 тригер.
5. Параметри кожного вузла відповідають його схемі (рядок `{{ }}` допустимий замість будь-якого типу). Для `mcp.tool` схема — `inputSchema` tool'а з кешу; якщо сервер невідомий або tool не знайдено — помилка з кодом `MCP_TOOL_UNKNOWN`.
6. `serverId`/`credentialId` існують і доступні користувачу; у сценарії проєкту — лише спільні або цього проєкту
   (`PROJECT_REFERENCE_FORBIDDEN`, F6-14; перевіряється при створенні, збереженні, активації й перенесенні, не в
   `validate_workflow` без сценарію).
7. `settings.executionTimeoutSec` не більший за серверну межу `OPENBP_MAX_EXECUTION_TIMEOUT_SEC` (`EXECUTION_TIMEOUT_TOO_LARGE`).
8. `settings.errorWorkflowId` — інший існуючий сценарій з `core.errorTrigger` (`ERROR_WORKFLOW_INVALID`).
   `core.executeWorkflow` — доступний сценарій з `core.workflowTrigger` (`SUBWORKFLOW_INVALID`), без циклу викликів
   (`SUBWORKFLOW_CYCLE`); див. «Підсценарії».
9. Бізнес-атрибути: `BUSINESS_LANE_ID_DUPLICATE` (`/business/lanes/{i}/id`), `BUSINESS_STEP_ID_DUPLICATE`
   (`/business/steps/{i}/id`), `BUSINESS_LANE_UNKNOWN` (`/business/steps/{i}/lane` або `/nodes/{i}/business/lane`),
   `BUSINESS_LINK_NODE_UNKNOWN` (`/business/steps/{i}/links/{j}/node`), `BUSINESS_STEP_LINKS_MISSING`
   (`/business/steps/{i}/links`).
10. Етапи (ADR-004): `STAGE_ID_DUPLICATE` (`/stages/{i}/id`), `STAGE_NODE_UNKNOWN` (`/stages/{i}/nodes/{j}`),
    `STAGE_NODE_IN_TWO_STAGES` (`/stages/{i}/nodes/{j}`, на другому етапі), `STAGE_NOT_CONNECTED` (`/stages/{i}/nodes`),
    `STAGE_TRANSITION_UNKNOWN` (`/stages/{i}/transitions/{k}/to`), `STAGE_RULE_ID_DUPLICATE`
    (`/stages/{i}/transitions/{k}/rules/{r}/id`); `owner.lane` без такої доріжки — `BUSINESS_LANE_UNKNOWN` (`/stages/{i}/owner/lane`).
11. `core.webhook` з `respond: "fromNode"` має досяжний увімкнений `core.respondToWebhook` (`WEBHOOK_RESPOND_NODE_MISSING`,
    `/nodes/{i}/parameters/respond`).

Помилки повертаються списком `{ code, nodeId?, path?, message }` — агент виправляє по кодах.

Окремо від помилок — попередження `warnings` (не заважають збереженню й активації): `VARIABLE_UNKNOWN` — `$vars.key` чи
`$vars["key"]` у параметрах (зокрема в коді `core.code`) посилається на змінну, якої немає; до її створення вираз дає
`undefined`.

## Експорт та імпорт

`GET /api/v1/workflows/{id}/export` (UI: «Експортувати» у списку сценаріїв, CLI: `openbp export`) віддає визначення
поточної версії і маніфест залежностей:

```json
{
  "definition": { "formatVersion": 1, "name": "Заявка з Telegram → 1С", "...": "..." },
  "manifest": {
    "connectors":  [{ "ref": "5f0c…", "name": "1С", "transport": "http" }],
    "credentials": [{ "ref": "9a12…", "name": "Anthropic", "type": "openai-compatible" }],
    "variables":   ["region"],
    "workflows":   [{ "ref": "77de…", "name": "Сповіщення про помилки" }]
  }
}
```

`ref` — id у середовищі-джерелі, як він записаний у визначенні; секретів і значень змінних у файлі немає.

`POST /api/v1/workflows/import { definition, manifest?, mapping?, groupId? }` (UI: «Імпорт», CLI: `openbp import`)
створює **новий неактивний** сценарій. Кожне посилання (`serverId` і `credentialId` будь-де в `parameters`,
`settings.errorWorkflowId`) отримує відповідник у цьому середовищі:

1. явний `mapping: { connectors: { "<ref>": "<id>" }, credentials: { … }, workflows: { … } }`;
2. інакше — єдиний об'єкт з тією самою назвою (без урахування регістру) і тим самим транспортом (конектор) чи типом
   (credential) з маніфесту;
3. без маніфесту посилання лишається, якщо такий id тут є.

Якщо для чогось відповідника немає, відповідь — 422 `IMPORT_UNMAPPED` зі списком
`unmapped: [{ kind: connector | credential | workflow, ref, name, type, reason: not_found | ambiguous | invalid }]`, і
нічого не створюється. Після перемапінгу сценарій проходить звичайну валідацію (422 `WORKFLOW_INVALID`). Відповідь
успіху — `{ workflow, mapping, missingVariables }`: `missingVariables` — ключі `$vars`, яких тут ще немає.

### Імпорт з n8n

`POST /api/v1/workflows/convert/n8n` (F6-24) приймає JSON сценарію n8n (файл з «Download» або копію вузлів з полотна) і
нічого не зберігає: відповідь `{ definition, manifest, notes }` — готове тіло для `POST /workflows/import`. UI-діалог
«Імпорт» сам розпізнає n8n (`connections` — об'єкт за назвами вузлів або типи `n8n-nodes-base.*` / `@n8n/*`), перетворює,
імпортує і показує `notes`. Код n8n не використовується — лише формат його JSON.

| n8n | OpenBP |
| --- | --- |
| Manual Trigger, Schedule Trigger (cron, хвилини/години/дні/тижні/місяці), Webhook | `core.manualTrigger`, `core.schedule`, `core.webhook` |
| Code (JavaScript) | `core.code`; `$input`, `$('Назва')`, legacy `items` і результат `[{ json }]` емулюються обгорткою |
| If (v1, v2), Merge (append, combine by position), Set, No Op, Wait (інтервал, дата) | `core.if`, `core.merge` v2, `core.set`, `core.wait` v2 |
| Data Table (insert, get, update, upsert, delete) | `core.dataTable` (таблицю шукає за назвою з n8n) |
| Telegram «Send Message» | `mcp.tool` конектора Telegram, `send_message` |
| Telegram «Send Photo» (URL або бінарні дані) | `mcp.tool` `send_photo` (F6-26) |
| Telegram «Send and Wait for Response» | `mcp.tool` `send_approval` → `mcp.waitFor` `get_approval` (умова `answered`, щохвилини); вихід `timeout` веде туди ж, куди й відповідь (F6-27) |
| Read RSS Feed | `mcp.tool` `rss_read` конектора HTTP, `itemsPath = items` (F6-25) |
| HTTP Request | `mcp.tool` `http_request`; відповідь `{ status, json \| text \| base64 }` розгортається службовим `core.code` у форму n8n — JSON-тіло полями item'а |
| Ollama (`@n8n/n8n-nodes-langchain.ollama`) | `ai.llm`, провайдер `openai-compatible`; відповідь у `$json.text` (у `notes` — де перевірити код, що читав `response`/`message.content`) |

- Вирази `={{ … }}` → `{{ … }}`; `$('Назва').item.json`, `.first().json`, `.last().json` → `$node["Назва"].json`.
- Вирази Luxon (`$now.setZone(tz).toISODate()`, `.toISO()`, `.toFormat(…)`, `$today`) переписуються на `Date` у поясі
  сценарію (у виразах і в коді); решта Luxon лишається зауваженням `check`. `$json.data.approved` після «Send and Wait»
  стає `$json.approved` (без відповіді поле порожнє — «не погоджено»).
- «Execute Once» → службовий `core.code` «<вузол>: лише перший item» перед вузлом.
- Вузол без відповідника (інші ШІ-вузли, Gmail, Sheets…) стає заглушкою
  `core.code` з тією самою назвою, що кидає помилку; зв'язки зберігаються, у `notes` вузла — тип і параметри n8n.
- `onError: continueErrorOutput` → `continueOnFail` і вихід помилки; `continueRegularOutput` → `continueOnFail`, а вихід
  помилки веде туди ж, куди основний. `retryOnFail` → `retry`. Ребро, що замикає цикл, відкидається (DAG).
- Конектори Telegram і HTTP та credential Ollama потрапляють у `manifest` з назвами credentials n8n; транспорт конектора порожній —
  при імпорті підходить будь-який, а без збігу за назвою діалог просить вибрати відповідник.
- `notes: [{ level, node, message }]`: `replace` — заглушка, яку треба замінити; `check` — перенесено, але варто
  перевірити (Execute Once, Luxon у виразах, `Buffer` у коді, таблиці даних, які треба створити, тощо).
