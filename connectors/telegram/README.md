# Конектор Telegram Bot API для AI_BPMS

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
| `send_document` | `sendDocument` | `chat_id`, один з `document_url` / `file_id` / `document_base64` (до 50 МБ) + `file_name?`, `caption?` (до 1024 символів), `parse_mode?`, `reply_markup?` | надіслане повідомлення з `document` |
| `send_video` | `sendVideo` | як `send_document` (`video_url` / `file_id` / `video_base64`) + `duration?`, `width?`, `height?`, `supports_streaming?` | надіслане повідомлення з `video` |
| `send_audio` | `sendAudio` | як `send_document` (`audio_url` / `file_id` / `audio_base64`) + `duration?`, `performer?`, `title?` | надіслане повідомлення з `audio` |
| `send_voice` | `sendVoice` | як `send_document` (`voice_url` / `file_id` / `voice_base64`; OGG/OPUS, MP3 чи M4A) + `duration?` | надіслане повідомлення з `voice` |
| `send_photo` | `sendPhoto` | `chat_id`, `photo_url` **або** `photo_base64` (до 10 МБ, JPEG/PNG) + `file_name?`, `caption?` (до 1024 символів), `parse_mode?`, `reply_markup?` | надіслане повідомлення з `photo` (розміри з `file_id`); задовгий підпис чи завелика картинка — `isError` з поясненням, без запиту до Bot API |
| `send_approval` | `sendMessage` з кнопками | `chat_id`, `text`, `approve_label?` (типово «✅ Погодити»), `reject_label?` (типово «❌ Відхилити»), `parse_mode?` | `{ message_id, chat_id, token }` |
| `get_approval` | `getUpdates` без `offset` | `chat_id`, `message_id`, `token` (з `send_approval`) | `{ answered: false }` або `{ answered: true, approved, decision: "approve" \| "reject", by: { id, username, first_name, last_name } }` |
| `get_updates` | `getUpdates` | `offset?`, `limit?` (1–100) | `{ "result": [Update, …] }`, кожне з унікальним `update_id` |
| `get_chat` | `getChat` | `chat_id` | `id`, `type`, `title`/`username`/`first_name` |

У конструкторі AI_BPMS `reply_markup` редагується візуально (інлайн-кнопки чи клавіатура, `x-openbp-widget: keyboard`), `commands` — таблицею, `parse_mode` — списком, `text`/`caption` мають лічильник символів (docs/connectors.md §2).

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

## 3. Зареєструвати в AI_BPMS

«Конектори» → «Додати MCP-сервер» (роль admin):

- **stdio** (AI_BPMS запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.Telegram.dll`;
  - credential конектора: `{ "env": { "TELEGRAM_BOT_TOKEN": "123456789:AA…" } }`. AI_BPMS зберігає його
    зашифрованим і передає процесу як змінну середовища.
- **http** (окремий сервіс, напр. контейнер):
  - `TELEGRAM_BOT_TOKEN=… dotnet out/OpenBP.Connectors.Telegram.dll --http http://0.0.0.0:8080`;
  - в AI_BPMS: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і дев'ять tools. Інша адреса Bot API (локальний
[Bot API server](https://github.com/tdlib/telegram-bot-api)) задається змінною `Connector__BaseUrl`.

Для CLI (`openbp run`) запис є в `examples/servers.json` (`srv_telegram_bot`): токен береться зі змінної
середовища вашої оболонки, у файлі його немає.

## 4. Отримувати повідомлення: три сценарії

Telegram доставляє оновлення **або** на webhook, **або** через `getUpdates`, не обома способами одночасно.

### Підписка (миттєво, без публічної адреси) — рекомендовано

Тригер «Нове повідомлення Telegram» з палітри — вузол `mcp.subscribe`: `resourceUri = telegram://updates`,
`itemsPath = result`, `keyField = update_id`. Поки сценарій активний, конектор сам тримає long-polling `getUpdates`
(`Connector:Events:LongPollSec`, 25 с), підтверджує отримане (`offset`) і одразу сповіщає AI_BPMS; без підписників
`getUpdates` не викликається. Повідомлення — у `$json.message`, натискання кнопки — у `$json.callback_query`.
Обмеження: підтверджені оновлення tool `get_updates` уже не побачить (один споживач на бота), а з заданим webhook
ресурс недоступний — підписка повертає помилку з поясненням; зніміть webhook tool'ом `delete_webhook`.

### Webhook (миттєво; потрібна публічна HTTPS-адреса AI_BPMS)

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

### Polling (опитування за розкладом)

1. Якщо webhook встановлено, зніміть його tool'ом `delete_webhook`.
2. Тригер `mcp.poll`: сервер Telegram, tool `get_updates`, `itemsPath = result`, `keyField = update_id`,
   `cron = */1 * * * *`. AI_BPMS пам'ятає оброблені `update_id` і запускає сценарій лише з новими.
3. Повідомлення — у `$json.message`. Без `offset` Telegram віддає непідтверджені оновлення до 24 годин, тож ті
   самі `update_id` приходять кілька разів — `mcp.poll` відкидає вже бачені за `keyField`.

## 5. Погодження з очікуванням відповіді (аналог n8n «Send and Wait»)

`send_approval` надсилає повідомлення з двома кнопками, `get_approval` перевіряє, чи натиснули одну з них. Сценарій:
`mcp.tool send_approval` → `mcp.waitFor get_approval` з умовою `answered isTrue` → `core.if` на `approved`; вихід
`timeout` вузла `mcp.waitFor` — «ніхто не відповів» (вважайте як «не погоджено»).

```json
{ "id": "n_ask", "name": "Запит на погодження", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
  "parameters": { "serverId": "srv_telegram", "toolName": "send_approval",
    "arguments": { "chat_id": 42, "text": "Погодити пост «{{ $json.title }}»?" } } },
{ "id": "n_wait", "name": "Чекати відповідь", "type": "mcp.waitFor", "typeVersion": 1, "position": [400, 0],
  "parameters": { "serverId": "srv_telegram", "toolName": "get_approval",
    "arguments": { "chat_id": "{{ $json.chat_id }}", "message_id": "{{ $json.message_id }}", "token": "{{ $json.token }}" },
    "conditions": [ { "field": "answered", "operator": "isTrue" } ], "intervalMinutes": 1, "timeoutHours": 4 } },
{ "id": "n_if", "name": "Погоджено?", "type": "core.if", "typeVersion": 1, "position": [600, 0],
  "parameters": { "conditions": [ { "left": "{{ $json.approved }}", "operator": "isTrue" } ], "combine": "and" } }
```

Як це працює:

- кнопки несуть `callback_data` = `apv:<token>:1` (погодити) чи `apv:<token>:0` (відхилити); токен випадковий для
  кожного запиту, тож натискання в іншому повідомленні чи з чужим токеном не зараховуються;
- коли кнопку натиснули, `get_approval` відповідає на натискання («Рішення записано»), прибирає кнопки й дописує в
  повідомлення «✅ Погоджено: Олена (@olena)» або «❌ Відхилено: …» (розмітка оригіналу зберігається);
- зараховується перше натискання; після нього кнопок уже немає. Якщо Telegram відмовив у відповіді чи редагуванні
  (натискання давнє, текст задовгий) — рішення все одно повертається, а кнопки прибираються окремим запитом.

Обмеження:

- **Один споживач оновлень на бота.** `get_approval` читає непідтверджені оновлення через `getUpdates` і нічого не
  підтверджує. Поки боту встановлено webhook, `getUpdates` не працює — `get_approval` поверне помилку з поясненням;
  зніміть webhook (`delete_webhook`) або візьміть окремого бота для погоджень. Тригер `mcp.poll` на `get_updates` без
  `offset` (розділ 4) поруч працює.
- Telegram тримає непідтверджені оновлення 24 години і віддає не більше 100: погодження чекайте не довше доби й не
  на боті з великим потоком повідомлень без споживача.
- У групі натиснути кнопку може будь-хто з учасників; хто саме — у `by`.

## Файли: `file_id`, посилання чи вміст

`send_document`, `send_video`, `send_audio` і `send_voice` приймають рівно один спосіб передати файл: посилання (Telegram
сам його завантажує), `file_id` (файл, який у Telegram уже є: його надіслав користувач або бот раніше) чи вміст у base64
(до 50 МБ — ліміт Bot API на завантаження). Вхідне повідомлення з файлом у події
`telegram://updates` (і в `get_updates`) має `document` / `video` / `audio` / `voice` з `file_id`, `mime_type` і (для
документів) `file_name`. Помилки (невідомий чат, закритий бот, завеликий файл) повертаються текстом без токена.
