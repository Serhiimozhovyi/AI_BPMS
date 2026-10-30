# Конектор Microsoft Graph (пошта й Excel) для AI_BPMS

MCP-сервер над [Microsoft Graph](https://learn.microsoft.com/graph/api/resources/mail-api-overview) для пошти
Microsoft 365 і книг Excel у SharePoint/OneDrive (розділ «Excel» нижче), зроблений на шаблоні Connector Kit (`tools/connector-template`). OpenAPI Graph завеликий для генератора,
тому tools описано вручну:

| Tool | Запит Graph | Аргументи | Результат |
|---|---|---|---|
| `list_messages` | `GET /users/{mailbox}/[mailFolders/{folder}/]messages` | `mailbox`, `folder?` (`inbox`, `sentitems`…), `filter?` (OData `$filter`), `since?` (ISO 8601), `top?` (1–1000, типово 25) | `{ "result": [Message, …] }` з `id`, `subject`, `from`, `receivedDateTime`, `hasAttachments`, `bodyPreview` |
| `get_message` | `GET /users/{mailbox}/messages/{id}` | `mailbox`, `id` | лист з тілом (`body.contentType`, `body.content`) |
| `list_attachments` | `GET …/messages/{id}/attachments` | `mailbox`, `message_id` | `{ "result": [{ id, name, contentType, size, isInline }] }` без вмісту |
| `get_attachment` | `GET …/attachments/{id}` | `mailbox`, `message_id`, `attachment_id` | `resource`-блок (blob, назва файлу, MIME) → у AI_BPMS це binary item |
| `send_mail` | `POST /users/{from}/sendMail` | `from`, `to[]`, `subject`, `html`, `cc?`, `attachments?` (`[{ name, content_base64, content_type? }]`, до 3 МБ кожне) | `{ sent, from, to }` |

`list_messages` без власного `filter` віддає найновіші листи першими. Для тригера `mcp.poll`: tool `list_messages`,
`itemsPath = result`, `keyField = id`; `since` обмежує вибірку, щоб не тягнути всю скриньку.

**Нові листи без розкладу** — тригер «Новий лист» з палітри, вузол `mcp.subscribe` з ресурсом
`msgraph://mail/<скринька>/inbox` (напр. `msgraph://mail/orders@firma.ua/inbox`), `itemsPath = result`, `keyField = id`.
Поки сценарій активний, конектор раз на `Connector:Events:IntervalSec` (60 с) читає «Вхідні» після останнього
`receivedDateTime` і сповіщає AI_BPMS про нові листи (подія — лист у формі `list_messages`). Graph change notifications
не використовуються: їм потрібна публічна HTTPS-адреса конектора й поновлення підписки щодоби.

Автентифікація — client credentials (застосунок без користувача). Токен кешується до хвилини перед закінченням; якщо
Graph відповів 401, конектор один раз бере новий токен і повторює запит. 429 і 503 повертаються як `isError` з
«Повторіть через N с (Retry-After)» — повтор вирішує сценарій (налаштування повторів вузла). Секрету застосунку й
токенів немає ні у відповідях, ні в текстах помилок, ні в журналах.

> Повідомлення в канали Teams через Graph з application permissions надсилати не можна (`ChannelMessage.Send` —
> лише делегований дозвіл), тому tool `send_teams_message` у цьому конекторі поки немає.

## Excel у SharePoint/OneDrive (F3-32)

Ті самі tools і аргументи, що й у конекторі Google Sheets (`connectors/gsheets`), тож сценарій перемикається між
Google-таблицею й книгою Excel лише зміною `serverId` (і `spreadsheet_id`). Перший рядок використаного діапазону
листа — заголовок, рядки адресуються номером, як у книзі, значення — за назвами колонок.

| Tool | Запит Graph | Аргументи | Результат |
|---|---|---|---|
| `read_sheets` | `GET …/workbook/worksheets/{лист}/usedRange(valuesOnly=true)` | `spreadsheet_id`, `sheets[]` | `{ sheets: { <лист>: [{ row_number, <колонка>: значення }] }, headers }` |
| `update_row` | `PATCH …/worksheets/{лист}/range(address='C4')` | `spreadsheet_id`, `sheet`, `row_number`, `values{}`, `value_input?` | `{ row_number, updated }` |
| `append_row` | `POST …/workbook/tables/{id}/rows/add` | `spreadsheet_id`, `sheet`, `values{}`, `value_input?` | `{ row_number }` |
| `upsert_row` | пошук ключа в `usedRange` → `update_row` або `append_row` | `spreadsheet_id`, `sheet`, `key_column`, `values{}`, `value_input?` | `{ row_number, action }` |
| `append_rows` | `POST …/tables/{id}/rows/add` | `spreadsheet_id`, `range` (назва листа), `values[][]`, `value_input?` | `{ updated_range, updated_rows }` |

- `spreadsheet_id` — `"<drive_id>/<item_id>"` або посилання на файл із SharePoint/OneDrive («Поділитися» → «Копіювати
  посилання»); посилання конектор перетворює на файл через `GET /shares/{u!…}/driveItem`.
- **Дописувати рядки можна лише в таблицю Excel** на листі: `rows/add` атомарний, а запис «після останнього рядка
  usedRange» при двох одночасних запусках затирав би рядок. Якщо таблиці немає, `append_row`, `append_rows` і
  `upsert_row` з новим ключем повертають `isError` з підказкою: виділіть заголовок і дані → «Вставлення» → «Таблиця» →
  «Таблиця із заголовками». Оновлення наявного рядка таблиці не потребує.
- Записи йдуть в одній сесії книги (`createSession` з `persistChanges: true`) на виклик tool'а, сесія закривається
  завжди; читання — без сесії.
- `value_input`: `RAW` (типово) — текст, схожий на формулу, число чи дату (`=…`, `007`, `2026-10-26`), записується з
  префіксом `'` і лишається текстом (захист від формул у даних від користувачів бота); `USER_ENTERED` — як набрано в
  Excel. `read_sheets` повертає значення без форматування: дати — числами Excel (серійний номер дня).
- 423 (книга заблокована), 429 і 503 повертаються як `isError` з «Повторіть через N с (Retry-After)».

## 1. Зареєструвати застосунок в Entra ID

1. [Entra admin center](https://entra.microsoft.com) → «Applications» → «App registrations» → «New registration».
   Назва — напр. `AI_BPMS`, «Accounts in this organizational directory only», Redirect URI не потрібен.
2. На сторінці застосунку збережіть **Directory (tenant) ID** і **Application (client) ID**.
3. «Certificates & secrets» → «New client secret». Збережіть **Value** (не Secret ID): це секрет, як пароль; він
   показується один раз і має термін дії — поставте нагадування оновити.
4. «API permissions» → «Add a permission» → «Microsoft Graph» → **Application permissions**:
   - `Mail.Read` — для `list_messages`, `get_message`, `list_attachments`, `get_attachment`;
   - `Mail.Send` — для `send_mail`.

   - для Excel — `Sites.Selected` (доступ лише до сайтів, які видано явно, див. нижче); запасний варіант, якщо
     адміністратор не може видати доступ до сайту, — `Files.ReadWrite.All` (усі файли організації).

   Потім «Grant admin consent for …» (потрібна роль адміністратора).
5. Рекомендовано обмежити застосунок потрібними скриньками, бо application permissions типово дають доступ до всіх
   скриньок організації:
   [RBAC for Applications в Exchange Online](https://learn.microsoft.com/exchange/permissions-exo/application-rbac)
   (або старіший `New-ApplicationAccessPolicy`).

### Доступ до сайту SharePoint для `Sites.Selected`

`Sites.Selected` сам нічого не відкриває: адміністратор видає застосунку роль на конкретний сайт. Найпростіше — у
[Graph Explorer](https://developer.microsoft.com/graph/graph-explorer) під обліковим записом адміністратора (з дозволом
`Sites.FullControl.All`):

1. Знайти id сайту: `GET https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com:/sites/psy` → поле `id`.
2. Видати доступ на запис:

   ```http
   POST https://graph.microsoft.com/v1.0/sites/{site-id}/permissions
   { "roles": ["write"], "grantedToIdentities": [{ "application": { "id": "<CLIENT_ID>", "displayName": "AI_BPMS" } }] }
   ```

   (або PowerShell PnP: `Grant-PnPAzureADAppSitePermission -AppId <CLIENT_ID> -DisplayName AI_BPMS -Site <url> -Permissions Write`).

`drive_id` і `item_id` файлу: `GET /sites/{site-id}/drive/root:/Бот/psy.xlsx` → `parentReference.driveId` і `id`; або
просто передайте посилання на файл у `spreadsheet_id`.

## 2. Зібрати

Потрібен .NET SDK 10.

```bash
cd connectors/msgraph
dotnet test
dotnet publish src/OpenBP.Connectors.MsGraph -c Release -o out
```

## 3. Зареєструвати в AI_BPMS

«Конектори» → «Додати MCP-сервер» (роль admin):

- **stdio** (AI_BPMS запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.MsGraph.dll`;
  - credential конектора:
    `{ "env": { "TENANT_ID": "…", "CLIENT_ID": "…", "CLIENT_SECRET": "…" } }`. AI_BPMS зберігає його зашифрованим і
    передає процесу як змінні середовища.
- **http** (окремий сервіс, напр. контейнер):
  - `TENANT_ID=… CLIENT_ID=… CLIENT_SECRET=… dotnet out/OpenBP.Connectors.MsGraph.dll --http http://0.0.0.0:8080`;
  - в AI_BPMS: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і десять tools (п'ять пошти, п'ять Excel). Національні хмари задаються змінними
`Connector__GraphUrl`, `Connector__AuthorityUrl` і `Connector__Scope`.

## 4. Приклади

- **Вхідні листи з вкладеннями:** `mcp.poll` → `list_messages { mailbox: "orders@firma.ua", folder: "inbox",
  filter: "hasAttachments eq true" }`, `keyField = id` → `mcp.tool` `list_attachments` → `get_attachment`
  (файл далі йде в `ai.llm` чи 1С як binary item).
- **Надіслати звіт:** `mcp.tool` `send_mail { from: "reports@firma.ua", to: ["director@firma.ua"],
  subject: "Продажі за вчора", html: "{{ $json.table }}" }`.
- **Бот з Excel замість Google-таблиці:** той самий сценарій, що й з конектором `gsheets`, — `upsert_row { spreadsheet_id:
  "https://contoso.sharepoint.com/:x:/s/psy/…", sheet: "Clients", key_column: "ChatId", values: { … } }` з `serverId`
  цього конектора.
