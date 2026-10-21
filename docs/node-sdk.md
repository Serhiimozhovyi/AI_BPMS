# Контракт вузла (Node SDK)

Пакет: `packages/nodes-sdk`. Реалізації: `packages/nodes/<пакет>/<вузол>/`.

## Опис вузла

```ts
import { defineNode } from "@openbp/nodes-sdk";

export default defineNode({
  type: "telegram.sendMessage",
  version: 1,
  displayName: "Telegram: надіслати повідомлення",
  description: "Надсилає текстове повідомлення в чат або канал",
  icon: "telegram",            // ключ іконки з набору web
  group: "integration",        // trigger | logic | data | integration | ai
  inputs: ["main"],
  outputs: ["main"],
  credentials: [{ name: "telegramApi", required: true }],

  // JSON Schema параметрів — з неї канвас будує форму
  parameters: {
    type: "object",
    required: ["chatId", "text"],
    properties: {
      chatId: { type: "string", title: "ID чату" },
      text:   { type: "string", title: "Текст", "x-ui": { widget: "textarea" } },
      parseMode: { type: "string", enum: ["none", "HTML", "Markdown"], default: "none", title: "Форматування" }
    }
  },

  async execute(ctx) {
    const cred = await ctx.getCredential("telegramApi");
    const out = [];
    for (const [i, item] of ctx.items().entries()) {
      const p = ctx.params(i);   // параметри з уже обчисленими виразами для item i
      const res = await ctx.http({
        method: "POST",
        url: `https://api.telegram.org/bot${cred.token}/sendMessage`,
        body: { chat_id: p.chatId, text: p.text, parse_mode: p.parseMode === "none" ? undefined : p.parseMode }
      });
      out.push({ json: res.body });
    }
    return [out];                // по масиву items на кожен вихід
  }
});
```

## Контекст `ctx`

| Метод | Що робить |
|---|---|
| `items(input = 0)` | items з вхідного порту |
| `params(itemIndex)` | параметри з обчисленими `{{ }}` для конкретного item |
| `getCredential(name)` | розшифровані дані credentials (тільки у воркері) |
| `http(req)` | HTTP-клієнт з таймаутом, повторами, логуванням без секретів |
| `logger` | логи, що потрапляють у журнал виконання |
| `signal` | AbortSignal — вузол має зупинятися при скасуванні/таймауті |
| `binary.read(ref)` / `binary.write(buf, meta)` | робота з файлами |
| `nodeState` | для тригерів з polling: збережений між запусками стан (напр. останній id) |

## Тригери

Тригер-вузол має `group: "trigger"`, `inputs: []` і додатково один з хуків:
- `webhook: { method, path }` — api реєструє маршрут, payload стає першим item;
- `schedule(params) → cron` — api реєструє repeatable job;
- `poll(ctx) → items` — викликається за розкладом, повертає лише нові записи (використовує `nodeState`).

## Вимоги до кожного вузла

- тест на успішний сценарій і на помилку (HTTP мокається через `msw` або `nock`);
- жодних секретів у `logger`, у текстах помилок та у вихідних items;
- поважає `ctx.signal`;
- обробляє кожен item окремо (не тільки перший);
- `version` збільшується при несумісній зміні параметрів; стара версія лишається в коді.

## Перенесення з Activepieces

Activepieces pieces (MIT) мають схожу ідею (props + run). При перенесенні:
1. переписати props у JSON Schema;
2. замінити їхній `context` на наш `ctx`;
3. додати запис в `THIRD_PARTY_NOTICES.md` з посиланням на оригінал.
