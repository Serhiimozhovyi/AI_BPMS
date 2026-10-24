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
- `type` — `<пакет>.<вузол>`: `core.*`, `mcp.tool`, `mcp.poll`, `ai.llm`. `typeVersion` — ціле; старі версії підтримуються, поки є сценарії, що їх використовують.
- `connections` — масив ребер; один вихід → кілька ребер (розгалуження), один вхід ← кілька ребер (items об'єднуються в порядку надходження). Індекс виходу відповідає `Descriptor.Outputs`; у вузлів з `continueOnFail: true` з'являється додатковий останній вихід `error`.
- Цикли заборонені (валідатор перевіряє DAG). Ітерація — через `core.loop`.
- `pinData` використовується тільки в ручному тестовому запуску.
- Секретів у форматі немає: лише `credentialId` і `serverId`. При імпорті в інше середовище посилання перемаплюються в UI.
- `timeoutSec` на вузлі перекриває дефолт (30 с для `mcp.tool`, 60 с для `ai.llm`, без ліміту для логіки).

## Обов'язкові поля

- Сценарій: `formatVersion` (= 1), `name`, `settings`, `nodes`, `connections`, `pinData` (може бути `{}`).
- `settings`: `timezone`, `executionTimeoutSec`, `errorWorkflowId` (`null`, якщо немає), `saveSuccessfulData`.
  У редакторі їх змінює діалог «Налаштування» (кнопка на панелі інструментів).
- Вузол: `id`, `name`, `type`, `typeVersion` (≥ 1), `position` (`[x, y]`), `parameters` (об'єкт). Необов'язкові: `timeoutSec`, `retry` (`maxTries`, `waitMs`), `continueOnFail`, `disabled`, `notes`.
- Ребро: `from.node` + `from.output`, `to.node` + `to.input` (індекси від 0).
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
6. `serverId`/`credentialId` існують і доступні користувачу.
7. `settings.executionTimeoutSec` не більший за серверну межу `OPENBP_MAX_EXECUTION_TIMEOUT_SEC` (`EXECUTION_TIMEOUT_TOO_LARGE`).
8. `settings.errorWorkflowId` — інший існуючий сценарій з `core.errorTrigger` (`ERROR_WORKFLOW_INVALID`).

Помилки повертаються списком `{ code, nodeId?, path?, message }` — агент виправляє по кодах.
