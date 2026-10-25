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
- `type` — `<пакет>.<вузол>`: `core.*`, `mcp.tool`, `mcp.poll`, `mcp.waitFor`, `ai.llm`, `ai.agent`. `typeVersion` — ціле; старі версії підтримуються, поки є сценарії, що їх використовують.
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
- Версія 2 (`typeVersion: 2`, палітра пропонує її для нових вузлів, F5-02): `mode: "duration"` — `amount` у `unit`
  (`seconds`, `minutes`, `hours`, `days`); `mode: "until"` — `until`, дата ISO 8601 (можна вираз), без зсуву — у
  `settings.timezone`. Найдовше — 90 днів; минула дата — items ідуть далі одразу. До 30 с вузол чекає в пам'яті, довше —
  виконання переходить у стан `waiting` до `resumeAt`, воркер вільний, а в призначений час виконання прокидається й
  продовжує з наступного вузла (docs/architecture.md §10.4 «Довге очікування»). Скасувати `waiting` можна з журналу
  виконань, REST або MCP `cancel_execution`.

```json
{ "id": "n_wait", "name": "Чекати 2 дні", "type": "core.wait", "typeVersion": 2, "position": [400, 0],
  "parameters": { "mode": "duration", "amount": 2, "unit": "days" } }
```

Чекати не часу, а події в зовнішній системі (задачу закрили, оплата надійшла) — `mcp.waitFor` (F5-03): перевіряє умову
над відповіддю tool'а конектора з інтервалом, між перевірками виконання теж у `waiting`; виходи `done` і `timeout`
(docs/connectors.md §3.1).

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

- Тригери `core.webhook` і `mcp.poll` мають необов'язковий параметр `correlationKey` — вираз над item'ом тригера,
  напр. `{{ $json.body.deal?.id }}` або `{{ $json.Id }}`. Рядок і число стають текстом, об'єкт — JSON, порожнє значення —
  «без ключа»; ключ обрізається пробілами й до 200 символів.
- Webhook: помилка виразу — відповідь 400. `mcp.poll` з ключем запускає **окреме виконання на кожен новий запис**
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
9. Бізнес-атрибути: `BUSINESS_LANE_ID_DUPLICATE` (`/business/lanes/{i}/id`), `BUSINESS_STEP_ID_DUPLICATE`
   (`/business/steps/{i}/id`), `BUSINESS_LANE_UNKNOWN` (`/business/steps/{i}/lane` або `/nodes/{i}/business/lane`),
   `BUSINESS_LINK_NODE_UNKNOWN` (`/business/steps/{i}/links/{j}/node`), `BUSINESS_STEP_LINKS_MISSING`
   (`/business/steps/{i}/links`).

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
