# Конектор Microsoft Graph (пошта) для OpenBP

MCP-сервер над [Microsoft Graph](https://learn.microsoft.com/graph/api/resources/mail-api-overview) для пошти
Microsoft 365, зроблений на шаблоні Connector Kit (`tools/connector-template`). OpenAPI Graph завеликий для генератора,
тому tools описано вручну:

| Tool | Запит Graph | Аргументи | Результат |
|---|---|---|---|
| `list_messages` | `GET /users/{mailbox}/[mailFolders/{folder}/]messages` | `mailbox`, `folder?` (`inbox`, `sentitems`…), `filter?` (OData `$filter`), `since?` (ISO 8601), `top?` (1–1000, типово 25) | `{ "result": [Message, …] }` з `id`, `subject`, `from`, `receivedDateTime`, `hasAttachments`, `bodyPreview` |
| `get_message` | `GET /users/{mailbox}/messages/{id}` | `mailbox`, `id` | лист з тілом (`body.contentType`, `body.content`) |
| `list_attachments` | `GET …/messages/{id}/attachments` | `mailbox`, `message_id` | `{ "result": [{ id, name, contentType, size, isInline }] }` без вмісту |
| `get_attachment` | `GET …/attachments/{id}` | `mailbox`, `message_id`, `attachment_id` | `resource`-блок (blob, назва файлу, MIME) → у OpenBP це binary item |
| `send_mail` | `POST /users/{from}/sendMail` | `from`, `to[]`, `subject`, `html`, `cc?`, `attachments?` (`[{ name, content_base64, content_type? }]`, до 3 МБ кожне) | `{ sent, from, to }` |

`list_messages` без власного `filter` віддає найновіші листи першими. Для тригера `mcp.poll`: tool `list_messages`,
`itemsPath = result`, `keyField = id`; `since` обмежує вибірку, щоб не тягнути всю скриньку.

**Нові листи без розкладу** — тригер «Новий лист» з палітри, вузол `mcp.subscribe` з ресурсом
`msgraph://mail/<скринька>/inbox` (напр. `msgraph://mail/orders@firma.ua/inbox`), `itemsPath = result`, `keyField = id`.
Поки сценарій активний, конектор раз на `Connector:Events:IntervalSec` (60 с) читає «Вхідні» після останнього
`receivedDateTime` і сповіщає OpenBP про нові листи (подія — лист у формі `list_messages`). Graph change notifications
не використовуються: їм потрібна публічна HTTPS-адреса конектора й поновлення підписки щодоби.

Автентифікація — client credentials (застосунок без користувача). Токен кешується до хвилини перед закінченням; якщо
Graph відповів 401, конектор один раз бере новий токен і повторює запит. 429 і 503 повертаються як `isError` з
«Повторіть через N с (Retry-After)» — повтор вирішує сценарій (налаштування повторів вузла). Секрету застосунку й
токенів немає ні у відповідях, ні в текстах помилок, ні в журналах.

> Повідомлення в канали Teams через Graph з application permissions надсилати не можна (`ChannelMessage.Send` —
> лише делегований дозвіл), тому tool `send_teams_message` у цьому конекторі поки немає.

## 1. Зареєструвати застосунок в Entra ID

1. [Entra admin center](https://entra.microsoft.com) → «Applications» → «App registrations» → «New registration».
   Назва — напр. `OpenBP`, «Accounts in this organizational directory only», Redirect URI не потрібен.
2. На сторінці застосунку збережіть **Directory (tenant) ID** і **Application (client) ID**.
3. «Certificates & secrets» → «New client secret». Збережіть **Value** (не Secret ID): це секрет, як пароль; він
   показується один раз і має термін дії — поставте нагадування оновити.
4. «API permissions» → «Add a permission» → «Microsoft Graph» → **Application permissions**:
   - `Mail.Read` — для `list_messages`, `get_message`, `list_attachments`, `get_attachment`;
   - `Mail.Send` — для `send_mail`.

   Потім «Grant admin consent for …» (потрібна роль адміністратора).
5. Рекомендовано обмежити застосунок потрібними скриньками, бо application permissions типово дають доступ до всіх
   скриньок організації:
   [RBAC for Applications в Exchange Online](https://learn.microsoft.com/exchange/permissions-exo/application-rbac)
   (або старіший `New-ApplicationAccessPolicy`).

## 2. Зібрати

Потрібен .NET SDK 10.

```bash
cd connectors/msgraph
dotnet test
dotnet publish src/OpenBP.Connectors.MsGraph -c Release -o out
```

## 3. Зареєструвати в OpenBP

«Конектори» → «Додати MCP-сервер» (роль admin):

- **stdio** (OpenBP запускає процес сам):
  - команда `dotnet`, аргументи — шлях до `out/OpenBP.Connectors.MsGraph.dll`;
  - credential конектора:
    `{ "env": { "TENANT_ID": "…", "CLIENT_ID": "…", "CLIENT_SECRET": "…" } }`. OpenBP зберігає його зашифрованим і
    передає процесу як змінні середовища.
- **http** (окремий сервіс, напр. контейнер):
  - `TENANT_ID=… CLIENT_ID=… CLIENT_SECRET=… dotnet out/OpenBP.Connectors.MsGraph.dll --http http://0.0.0.0:8080`;
  - в OpenBP: транспорт `http`, URL `http://<хост>:8080/mcp`.

Натисніть «Оновити tools»: стан «Підключено» і п'ять tools. Національні хмари задаються змінними
`Connector__GraphUrl`, `Connector__AuthorityUrl` і `Connector__Scope`.

## 4. Приклади

- **Вхідні листи з вкладеннями:** `mcp.poll` → `list_messages { mailbox: "orders@firma.ua", folder: "inbox",
  filter: "hasAttachments eq true" }`, `keyField = id` → `mcp.tool` `list_attachments` → `get_attachment`
  (файл далі йде в `ai.llm` чи 1С як binary item).
- **Надіслати звіт:** `mcp.tool` `send_mail { from: "reports@firma.ua", to: ["director@firma.ua"],
  subject: "Продажі за вчора", html: "{{ $json.table }}" }`.
