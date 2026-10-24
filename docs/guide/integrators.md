# Посібник інтегратора

Як працювати з OpenBP з інших систем: REST API, webhook-и, MCP для ШІ-агентів, вбудований редактор і власні
конектори. Приклади нижче перевірено на продакшн-розгортанні з `docker-compose.prod.yml`.

```bash
export OPENBP_URL=https://openbp.example.com
export OPENBP_API_KEY=obp_…        # див. §1
```

## 1. Доступ: API-ключі

REST і `/mcp` приймають `Authorization: Bearer <ключ>`. Ключ створюється в UI («Налаштування» → «API-ключі») і
показується один раз. Ключ має роль свого користувача (`viewer` — читання, `editor` — сценарії й запуски, `admin` —
ще конектори й облікові дані). Для інтеграції заведіть окремого користувача з потрібною роллю: його ключ можна
відкликати, не чіпаючи людей. Відкликаний ключ — 401.

Ключ можна створити і через REST, з cookie-сесії користувача:

```bash
curl -fsS -c cookies.txt -H 'content-type: application/json' \
  -d '{"email":"integration@example.com","password":"…"}' "$OPENBP_URL/api/v1/auth/login"
curl -fsS -b cookies.txt -H 'content-type: application/json' \
  -d '{"name":"crm-sync"}' "$OPENBP_URL/api/v1/api-keys"          # → { "id", "name", "key": "obp_…" }
```

Якщо OpenBP налаштовано на вхід через Keycloak чи Entra ID, замість ключа приймається і access token провайдера
([docs/admin/oidc.md](../admin/oidc.md)).

## 2. REST API

Опис — OpenAPI 3.1: `$OPENBP_URL/openapi/v1.json` (без входу) і знімок [docs/openapi.json](../openapi.json). Клієнта
можна згенерувати будь-яким генератором OpenAPI. Помилки — Problem Details (RFC 9457) з машинним `code`:
`{ "status": 404, "title", "detail": "…", "code": "NOT_FOUND" }`. 401 — без ключа, 403 — бракує ролі, 409 — конфлікт,
422 `WORKFLOW_INVALID` — невалідний сценарій, з `errors: [{ "code", "message", "nodeId", "path": "/nodes/0/type" }]`.

Створити сценарій з файлу у [форматі workflow](../workflow-format.md):

```bash
curl -fsS -X POST "$OPENBP_URL/api/v1/workflows" \
  -H "Authorization: Bearer $OPENBP_API_KEY" -H 'content-type: application/json' \
  --data @examples/hello-manual.json
# → { "id": "01a1…", "name": "Привіт, OpenBP", "version": 1, "active": false, "definition": { … }, … }
```

Запустити і прочитати результат:

```bash
curl -fsS -X POST "$OPENBP_URL/api/v1/workflows/<id>/executions" \
  -H "Authorization: Bearer $OPENBP_API_KEY" -H 'content-type: application/json' -d '{}'
# → { "id": "<executionId>", "state": "queued", "mode": "manual", … }

curl -fsS "$OPENBP_URL/api/v1/executions/<executionId>" -H "Authorization: Bearer $OPENBP_API_KEY"
# → { "summary": { "state": "success", "startedAt", "finishedAt", "error": null, … },
#     "steps": [ { "nodeId", "status", "input", "output": [[ { "json": { … } } ]], "error" }, … ] }
```

Стани: `queued`, `running`, `cancelling`, `success`, `error`, `cancelled`, `timed_out`. Свої дані на вхід —
`{ "startNodeId": "n_start", "startItems": [ { "json": { "orderId": 42 } } ] }` у тілі запуску.

Стежити за виконанням наживо — Server-Sent Events:

```bash
curl -N "$OPENBP_URL/api/v1/executions/<executionId>/events" -H "Authorization: Bearer $OPENBP_API_KEY"
# event: step     data: { "nodeId", "status", "output", … }      — по події на кожен крок
# event: status   data: { "executionId", "state": "success", "error": null }  — стан; після завершення потік закривається
```

Інше: `GET /api/v1/workflows` (список), `PUT /api/v1/workflows/{id}` (нова версія), `POST …/activate` і
`…/deactivate`, `GET /api/v1/executions?workflowId=…`, `POST /api/v1/executions/{id}/cancel` і `…/retry`,
`POST /api/v1/workflows/validate` з `{ "definition": { … } }` (перевірити без збереження). Повний перелік — в OpenAPI.

## 3. Webhook-и

Тригер «Webhook» (`core.webhook`) приймає запити на `$OPENBP_URL/webhook/<path>`, коли сценарій активний. Тіло, query
і заголовки стають першим item'ом: `{ "body", "query", "headers" }`. Відповідь — 202 `{ "executionId" }`, тобто
OpenBP ставить виконання в чергу й не чекає його завершення.

Захистіть webhook секретом: облікові дані типу «Webhook: токен у заголовку» (`{ "token": "…" }`) і в тригері
`auth: { "type": "header", "credentialId": "<id>" }`. Заголовок за замовчуванням — `X-Webhook-Token` (`headerName` —
інший, напр. `X-Telegram-Bot-Api-Secret-Token`). Є і `basic` з `{ username, password }`.

```bash
curl -sS -X POST "$OPENBP_URL/webhook/orders" \
  -H 'X-Webhook-Token: <секрет>' -H 'content-type: application/json' \
  -d '{"customer":" Петренко ","qty":"2"}'
# → 202 { "executionId": "01a1…" }
# без заголовка — 401; неактивний сценарій, інший шлях чи метод — 404
```

Заголовок із секретом і `cookie` в item не потрапляють.

## 4. MCP для ШІ-агентів

OpenBP — MCP-сервер на `$OPENBP_URL/mcp` (Streamable HTTP, без серверних сесій), з тим самим API-ключем. Агент бачить
конектори й типи вузлів, збирає, перевіряє й запускає сценарії: `describe_connectors`, `describe_nodes`,
`list_workflows`, `get_workflow`, `create_workflow`, `update_workflow`, `validate_workflow`, `list_workflow_groups`,
`create_workflow_group`, `move_workflow`, `run_workflow`, `get_execution`, `list_executions`, `cancel_execution`,
`test_node`. Права — ті самі, що в REST.

**Claude Code:**

```bash
claude mcp add --transport http openbp "$OPENBP_URL/mcp" --header "Authorization: Bearer $OPENBP_API_KEY"
```

**Claude Desktop** — через міст `mcp-remote` (потрібен Node.js), у `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "openbp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://openbp.example.com/mcp", "--header", "Authorization:${OPENBP_AUTH}"],
      "env": { "OPENBP_AUTH": "Bearer obp_ваш_ключ" }
    }
  }
}
```

Перевірити, що ключ працює, без агента:

```bash
curl -sS -X POST "$OPENBP_URL/mcp" -H "Authorization: Bearer $OPENBP_API_KEY" \
  -H 'content-type: application/json' -H 'accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
# → event: message / data: {"result":{"tools":[{"name":"describe_connectors",…
```

Деталі, resources і типовий діалог — [docs/mcp-server.md](../mcp-server.md).

## 5. Вбудований редактор `<openbp-canvas>`

Редактор сценарію можна вставити у свій застосунок (зокрема Angular 16 «Кіт 2») як веб-компонент:

```html
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Ubuntu:wght@400;500;700&display=swap" />
<link rel="stylesheet" href="https://fonts.googleapis.com/icon?family=Material+Symbols+Outlined" />
<script src="https://openbp.example.com/element/openbp-canvas.js" defer></script>

<openbp-canvas api-base="https://openbp.example.com" workflow-id="01a1…" style="display:block; height: 70vh"></openbp-canvas>

<script>
  const canvas = document.querySelector('openbp-canvas');
  canvas.token = tokenFromYourBackend; // API-ключ або OIDC access token; не вписуйте ключ у розмітку
  canvas.addEventListener('openbp-saved', (e) => console.log('версія', e.detail.version));
  canvas.addEventListener('openbp-error', (e) => console.error(e.detail.code, e.detail.message));
</script>
```

- Origin сторінки хоста адміністратор додає в `OPENBP_CORS_ORIGINS` (`https://kit.example.com`), інакше браузер
  заблокує запити.
- Шрифти Ubuntu і Material Symbols підключає хост: без них замість іконок буде текст.
- `readonly` — лише перегляд. Події `openbp-saved`, `openbp-run`, `openbp-error`.

Атрибути, Angular 16, автентифікація — [docs/embedding.md](../embedding.md).

## 6. Свій конектор

Інтеграції в OpenBP — лише MCP-сервери. Для своєї системи з REST чи OData API є Connector Kit — шаблон .NET-проєкту
(MIT, можна робити закритим):

```bash
dotnet new install <репозиторій OpenBP>/tools/connector-template
dotnet new openbp-connector -n Acme.Crm
cd Acme.Crm && dotnet test
```

- Tools описуються вручну (`[McpServerTool]`) або генеруються з OpenAPI-специфікації чи OData `$metadata`
  (`Connector:Source = openapi:<url>` / `odata:<url>`), зокрема для 1С.
- Адреса API — `Connector:BaseUrl`, автентифікація — `Connector:Auth:Type` (`none`, `basic`, `bearer`, `api-key`),
  секрети — лише змінні середовища `CONNECTOR_*`.
- Запуск: stdio (OpenBP сам стартує процес) або `--http http://0.0.0.0:8080` — окремим сервісом.

Підключення в OpenBP — «Конектори» → «Додати MCP-сервер» (роль admin) або REST:

```bash
curl -fsS -X POST "$OPENBP_URL/api/v1/connectors" \
  -H "Authorization: Bearer $OPENBP_API_KEY" -H 'content-type: application/json' \
  -d '{"name":"Acme CRM","transport":"http","config":{"url":"http://acme-crm:8080/mcp"}}'
# → { "summary": { "id", "status": "ok", "toolCount": … }, "tools": [ … ] }
```

Для секретних заголовків http-конектора — облікові дані «Конектор HTTP: заголовки» і `"credentialId"` у тілі. Як
запустити конектор контейнером поруч з OpenBP — [docs/admin/README.md §7](../admin/README.md#7-конектори-й-облікові-дані).
Повний шлях «від нуля за годину» — [tools/connector-template/README.md](../../tools/connector-template/README.md),
контракт — [docs/connectors.md §5](../connectors.md#5-connector-kit-toolsconnector-template).
