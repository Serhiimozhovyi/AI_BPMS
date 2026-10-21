# Формат workflow JSON (formatVersion 1)

Цей формат — джерело правди для двигуна, канвасу, імпорту/експорту і API.
Його zod-схема живе в `packages/shared/src/workflow.ts`.

## Приклад

```json
{
  "formatVersion": 1,
  "name": "Нова заявка → Telegram",
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
      "parameters": { "method": "POST", "path": "new-request" }
    },
    {
      "id": "n_if",
      "name": "Терміново?",
      "type": "core.if",
      "typeVersion": 1,
      "position": [360, 200],
      "parameters": {
        "conditions": [
          { "left": "{{ $json.body.priority }}", "operator": "equals", "right": "high" }
        ]
      }
    },
    {
      "id": "n_tg",
      "name": "Повідомити в Telegram",
      "type": "telegram.sendMessage",
      "typeVersion": 1,
      "position": [620, 120],
      "parameters": {
        "chatId": "-100123456",
        "text": "Термінова заявка №{{ $json.body.id }}"
      },
      "credentials": { "telegramApi": "cred_01H..." },
      "retry": { "maxTries": 3, "waitMs": 2000 },
      "continueOnFail": false,
      "disabled": false,
      "notes": ""
    }
  ],
  "connections": [
    { "from": { "node": "n_webhook", "output": 0 }, "to": { "node": "n_if", "input": 0 } },
    { "from": { "node": "n_if", "output": 0 }, "to": { "node": "n_tg", "input": 0 } }
  ],
  "pinData": {
    "n_webhook": [ { "json": { "body": { "id": 42, "priority": "high" } } } ]
  }
}
```

## Правила

- `id` вузла — стабільний, генерується при створенні, не змінюється при перейменуванні. Вирази посилаються на **назву** (`$node["Нова заявка"]`), тому при перейменуванні канвас оновлює вирази.
- `name` унікальний у межах сценарію.
- `type` — `<пакет>.<вузол>`, `typeVersion` — ціле число; старі версії вузлів підтримуються, поки є сценарії, що їх використовують.
- `connections` — масив ребер; один вихід може мати кілька ребер (розгалуження), один вхід — кілька ребер (дані об'єднуються в порядку надходження).
- Цикли в графі заборонені (валідатор перевіряє DAG). Ітерація — через вузол `core.loop`.
- `pinData` використовується тільки в ручному тестовому запуску, у бойових виконаннях ігнорується.
- Секретів у форматі немає: лише id credentials.

## Валідація при збереженні

1. JSON відповідає zod-схемі.
2. Усі `connections` посилаються на існуючі вузли й порти.
3. Граф без циклів.
4. Рівно один або більше тригерів для активного сценарію.
5. Параметри кожного вузла відповідають його схемі (з урахуванням того, що рядок `{{ }}` допустимий замість будь-якого типу).
