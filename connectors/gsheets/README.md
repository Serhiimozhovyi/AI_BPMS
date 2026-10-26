# Конектор Google Sheets для OpenBP

MCP-сервер над [Google Sheets API v4](https://developers.google.com/sheets/api/reference/rest) на шаблоні Connector
Kit (`tools/connector-template`). Tools — «табличний» контракт: перший рядок листа — заголовок, рядки адресуються
номером (як у таблиці, з 1), значення — за назвами колонок. Той самий набір tools має Excel у SharePoint/OneDrive
(F3-32, конектор `msgraph`), тож сценарій перемикається між сховищами лише зміною `serverId`.

| Tool | Аргументи | Результат |
|---|---|---|
| `read_sheets` | `spreadsheet_id`, `sheets[]` | `{ sheets: { <лист>: [{ row_number, <колонка>: значення }] }, headers: { <лист>: [колонка] } }` — значення без форматування (числа — числами, дати — рядком), порожні рядки пропускаються, відсутні клітинки — `""` |
| `update_row` | `spreadsheet_id`, `sheet`, `row_number` (від 2), `values{}`, `value_input?` | `{ row_number, updated }`; змінюються лише названі клітинки |
| `append_row` | `spreadsheet_id`, `sheet`, `values{}`, `value_input?` | `{ row_number }`; значення стають у порядку заголовка, інші колонки порожні |
| `upsert_row` | `spreadsheet_id`, `sheet`, `key_column`, `values{}` (з ключем), `value_input?` | `{ row_number, action: "updated" \| "appended" }` |
| `append_rows` | `spreadsheet_id`, `range` (напр. `Log`), `values[][]`, `value_input?` | `{ updated_range, updated_rows }` — сирі рядки без заголовка, для журналів |

`spreadsheet_id` — частина адреси таблиці між `/d/` і `/edit`. Колонка, якої немає в живому заголовку листа, — помилка
(`isError` з переліком наявних колонок), нічого не записується. `value_input`: `RAW` (типово, значення як є) або
`USER_ENTERED` (як набрано в інтерфейсі: формули, дати). Ключ в `upsert_row` порівнюється як текст: `"1003"` і `1003`
— той самий рядок.

Автентифікація — сервісний акаунт Google: конектор підписує JWT закритим ключем (RS256) і обмінює його на токен,
токен кешується до хвилини перед закінченням; якщо Sheets відповів 401, конектор один раз бере новий токен і
повторює запит. 403/404 повертаються як `isError` з підказкою відкрити таблицю для email сервісного акаунта; 429 і 503
— з «Повторіть через N с (Retry-After)» (повтор вирішує сценарій). Закритого ключа, JWT і токенів немає ні у
відповідях, ні в текстах помилок, ні в журналах.

## 1. Сервісний акаунт

1. [Google Cloud Console](https://console.cloud.google.com) → виберіть або створіть проєкт.
2. «APIs & Services» → «Library» → **Google Sheets API** → «Enable».
3. «IAM & Admin» → «Service accounts» → «Create service account»: назва, напр. `openbp`; ролі проєкту не потрібні.
4. На сторінці акаунта → «Keys» → «Add key» → «Create new key» → **JSON**. Завантажений файл — це секрет, як пароль:
   не надсилайте його поштою й не кладіть у репозиторій. Збережіть з нього `client_email`
   (`openbp@<проєкт>.iam.gserviceaccount.com`).

> Якщо політика організації забороняє ключі сервісних акаунтів (`iam.disableServiceAccountKeyCreation`), попросіть
> адміністратора Google Workspace зробити виняток для цього проєкту.

## 2. Доступ до таблиці

Сервісний акаунт бачить лише таблиці, відкриті йому явно: у таблиці «Поділитися» → вставте `client_email` → роль
**Редактор** (для лише читання — «Читач», тоді працює тільки `read_sheets`). Сповіщення можна не надсилати. Перший
рядок кожного листа, з яким працює сценарій, має містити назви колонок.

## 3. Зібрати

Потрібен .NET SDK 10.

```bash
cd connectors/gsheets
dotnet test
dotnet publish src/OpenBP.Connectors.GSheets -c Release -o out
```

## 4. Зареєструвати в OpenBP

«Конектори» → «Додати MCP-сервер» (роль admin):

- **stdio** (OpenBP запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.GSheets.dll`;
  - credential конектора: `{ "env": { "GOOGLE_SERVICE_ACCOUNT_JSON": "<вміст JSON-файлу ключа повністю>" } }`.
    OpenBP зберігає його зашифрованим і передає процесу як змінну середовища.
- **http** (окремий сервіс, напр. контейнер):
  - `GOOGLE_SERVICE_ACCOUNT_JSON="$(cat key.json)" dotnet out/OpenBP.Connectors.GSheets.dll --http http://0.0.0.0:8080`;
  - в OpenBP: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і п'ять tools. Адресу API можна змінити змінною `Connector__SheetsUrl`
(напр. для проксі), адресу обміну токена — `Connector__TokenUrl` (типово — `token_uri` з ключа).

## 5. Приклади

- **Клієнти бота:** `mcp.tool` `read_sheets { spreadsheet_id: "1AbC…", sheets: ["Clients"] }`, `itemsPath:
  "sheets.Clients"` → по item на рядок з `row_number`.
- **Зберегти стан користувача:** `mcp.tool` `upsert_row { sheet: "Clients", key_column: "ChatId", values: { ChatId:
  "{{ $json.chat.id }}", Status: "waiting" } }`.
- **Журнал:** `mcp.tool` `append_rows { range: "Log", values: [["{{ $now }}", "sent", "{{ $json.chat_id }}"]] }`.
