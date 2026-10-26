# Конектор Telegram Bot API для OpenBP

MCP-сервер над [Telegram Bot API](https://core.telegram.org/bots/api), зроблений на шаблоні Connector Kit
(`tools/connector-template`). Tools описано вручну:

| Tool | Метод Bot API | Аргументи | Результат |
|---|---|---|---|
| `send_message` | `sendMessage` | `chat_id`, `text`, `parse_mode?` (`HTML`, `MarkdownV2`), `reply_markup?`, `disable_link_preview?` | надіслане повідомлення (`message_id`, `chat`, `date`, `text`) |
| `send_messages` | `sendMessage` по черзі | `messages[]` — до 100 об'єктів з полями `send_message` | `{ results: [{ index, ok, message_id?, error?, permanent }] }`: невдале повідомлення не зупиняє решту і не робить виклик `isError`; `permanent` — бота заблоковано чи чату немає, повтор не допоможе |
| `edit_message_text` | `editMessageText` | `chat_id`, `message_id`, `text`, `parse_mode?`, `reply_markup?` | змінене повідомлення |
| `answer_callback_query` | `answerCallbackQuery` | `callback_query_id`, `text?`, `show_alert?` | `{ "ok": true }` |
| `set_my_commands` | `setMyCommands` | `commands` (`[{ command, description }]`), `scope?`, `language_code?` | `{ "ok": true }` |
| `set_webhook` | `setWebhook` | `url` (https), `secret_token?`, `allowed_updates?`, `drop_pending_updates?` | `{ "ok": true }` |
| `delete_webhook` | `deleteWebhook` | `drop_pending_updates?` | `{ "ok": true }` |
| `send_document` | `sendDocument` | `chat_id`, `document_url` **або** `document_base64` + `file_name?`, `caption?` | надіслане повідомлення з `document` |
| `send_photo` | `sendPhoto` | `chat_id`, `photo_url` **або** `photo_base64` (до 10 МБ, JPEG/PNG) + `file_name?`, `caption?` (до 1024 символів), `parse_mode?`, `reply_markup?` | надіслане повідомлення з `photo` (розміри з `file_id`); задовгий підпис чи завелика картинка — `isError` з поясненням, без запиту до Bot API |
| `get_updates` | `getUpdates` | `offset?`, `limit?` (1–100) | `{ "result": [Update, …] }`, кожне з унікальним `update_id` |
| `get_chat` | `getChat` | `chat_id` | `id`, `type`, `title`/`username`/`first_name` |

`chat_id` — числовий id чату або `@username` каналу. `reply_markup` — об'єкт Bot API як є (рядок з JSON теж
підходить), з рівно одним з полів:

- `{ "inline_keyboard": [[{ "text": "Записатися", "callback_data": "book:s1" }]] }` — кнопки під повідомленням;
  натискання приходить як `callback_query`, на нього треба відповісти `answer_callback_query`, інакше на кнопці
  крутиться годинник;
- `{ "keyboard": [[{ "text": "Поділитися номером", "request_contact": true }]], "resize_keyboard": true }` — клавіатура
  замість поля вводу; номер приходить у `message.contact`;
- `{ "remove_keyboard": true }` — прибрати її.

Помилки Bot API (невірний токен — 401, ліміт — 429 з `retry_after`, `400 message is not modified`, коли
`edit_message_text` нічого не змінює) повертаються як `isError` з HTTP-статусом і описом Telegram. Токена бота немає ні у відповідях, ні в текстах
помилок, ні в журналах (журнали `HttpClient` вимкнено, бо токен стоїть у шляху запиту).

## 1. Створити бота

1. У Telegram напишіть [@BotFather](https://t.me/BotFather) → `/newbot`, задайте назву й username.
2. Збережіть токен виду `123456789:AA…`: це секрет, як пароль.
3. Щоб бот бачив усі повідомлення в групі, а не лише команди: `/setprivacy` → `Disable`.

## 2. Зібрати

Потрібен .NET SDK 10.

```bash
cd connectors/telegram
dotnet test
dotnet publish src/OpenBP.Connectors.Telegram -c Release -o out
```

## 3. Зареєструвати в OpenBP

«Конектори» → «Додати MCP-сервер» (роль admin):

- **stdio** (OpenBP запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.Telegram.dll`;
  - credential конектора: `{ "env": { "TELEGRAM_BOT_TOKEN": "123456789:AA…" } }`. OpenBP зберігає його
    зашифрованим і передає процесу як змінну середовища.
- **http** (окремий сервіс, напр. контейнер):
  - `TELEGRAM_BOT_TOKEN=… dotnet out/OpenBP.Connectors.Telegram.dll --http http://0.0.0.0:8080`;
  - в OpenBP: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і дев'ять tools. Інша адреса Bot API (локальний
[Bot API server](https://github.com/tdlib/telegram-bot-api)) задається змінною `Connector__BaseUrl`.

Для CLI (`openbp run`) запис є в `examples/servers.json` (`srv_telegram_bot`): токен береться зі змінної
середовища вашої оболонки, у файлі його немає.

## 4. Отримувати повідомлення: два сценарії

Telegram доставляє оновлення **або** на webhook, **або** через `getUpdates`, не обома способами одночасно.

### Webhook (миттєво; потрібна публічна HTTPS-адреса OpenBP)

1. Тригер `core.webhook`: `method = POST`, `path = telegram/orders`, автентифікація `header` з
   `headerName = X-Telegram-Bot-Api-Secret-Token` і credential `{ "token": "<довільний секрет>" }`.
2. Активуйте сценарій і зареєструйте адресу в Telegram одноразовим викликом tool'а `set_webhook` (напр. з
   окремого сценарію з ручним тригером або з ШІ-агента через `/mcp`):
   `url = https://openbp.example.com/webhook/telegram/orders`, `secret_token = <той самий секрет>`,
   `allowed_updates = ["message", "callback_query"]`. Telegram надсилатиме секрет у заголовку
   `X-Telegram-Bot-Api-Secret-Token`, і `core.webhook` відхилятиме запити без нього. Те саме вручну:

   ```bash
   curl "https://api.telegram.org/bot<токен>/setWebhook" \
     -d url=https://openbp.example.com/webhook/telegram/orders \
     -d secret_token=<той самий секрет>
   ```

3. Повідомлення приходить у `$json.body.message` (`chat.id`, `text`), натискання inline-кнопки — у
   `$json.body.callback_query` (`id`, `data`, `message.message_id`, `from.id`). Відповідь — вузол `mcp.tool` →
   `send_message` з `chat_id = {{ $json.body.message.chat.id }}` (див. `examples/telegram-1c.json`); для кнопки —
   `answer_callback_query` з `callback_query_id = {{ $json.body.callback_query.id }}` і, щоб показати наступний екран
   у тому самому повідомленні, `edit_message_text` з `message_id = {{ $json.body.callback_query.message.message_id }}`.
4. Меню команд (`/start`, `/sos`…) задається один раз tool'ом `set_my_commands`.

### Polling (без публічної адреси)

1. Якщо webhook встановлено, зніміть його tool'ом `delete_webhook`.
2. Тригер `mcp.poll`: сервер Telegram, tool `get_updates`, `itemsPath = result`, `keyField = update_id`,
   `cron = */1 * * * *`. OpenBP пам'ятає оброблені `update_id` і запускає сценарій лише з новими.
3. Повідомлення — у `$json.message`. Без `offset` Telegram віддає непідтверджені оновлення до 24 годин, тож ті
   самі `update_id` приходять кілька разів — `mcp.poll` відкидає вже бачені за `keyField`.
