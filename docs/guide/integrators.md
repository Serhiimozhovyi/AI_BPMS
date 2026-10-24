# Посібник інтегратора

Як працювати з OpenBP з інших систем: REST API, webhook-и, MCP для ШІ-агентів, вбудований редактор, власні
конектори і перенесення сценаріїв між середовищами (CI/CD). Приклади нижче перевірено на продакшн-розгортанні з `docker-compose.prod.yml`.

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
`…/deactivate`, `GET /api/v1/executions?workflowId=…`, `POST /api/v1/executions/{id}/cancel`, `…/retry` і `…/resume` (продовжити з вузла, що впав),
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

## 7. Перенесення між середовищами і CI/CD

Сценарії зберігаються в git як файли експорту і розкочуються з dev на prod командами CLI (F6-09). Файл містить
визначення і маніфест залежностей — назви й типи конекторів і облікових даних, ключі змінних; секретів у ньому немає.
У середовищі-приймачі конектори й облікові дані шукаються за назвою і типом, тож назвіть їх однаково в обох
середовищах (або дайте файл відповідників `--map`). Формат і правила — [docs/workflow-format.md](../workflow-format.md#експорт-та-імпорт).

```bash
# CLI: dotnet run --project src/OpenBP.Cli -- <команда> або зібраний `openbp`
# адреса й ключ — з OPENBP_URL / OPENBP_API_KEY (див. початок посібника), ключ ролі editor
openbp export --all --out workflows/                 # кожен сценарій → workflows/<id>.json
openbp export 01a1… --out workflows/
openbp import workflows/01a1….json                   # → «Імпортовано: <новий id>  <назва>»
openbp import workflows/01a1….json --map prod-map.json
openbp activate <новий id>
```

`prod-map.json` — явні відповідники, коли назви різняться:

```json
{ "connectors": { "<id у dev>": "<id у prod>" }, "credentials": { "<id у dev>": "<id у prod>" } }
```

Коди виходу: 0 — успіх; 1 — імпорт без відповідників (CLI друкує, чого бракує: вид, id, назва, тип); 2 — помилка
аргументів або відповідь OpenBP з помилкою (зокрема 422 `WORKFLOW_INVALID` зі списком помилок). Імпорт завжди створює
новий неактивний сценарій; змінні (`$vars`), яких у приймачі немає, CLI перелічує окремо — додайте їх у
Налаштуваннях.

Приклад GitHub Actions: при зміні файлів у `workflows/` на гілці `main` імпортувати й активувати їх на prod.

```yaml
name: Розкотити сценарії OpenBP
on:
  push:
    branches: [main]
    paths: ['workflows/**.json']
jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production          # секрети середовища, ручне підтвердження за потреби
    env:
      OPENBP_URL: ${{ vars.OPENBP_URL }}
      OPENBP_API_KEY: ${{ secrets.OPENBP_API_KEY }}   # ключ editor; не передавайте його через --api-key
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 2 }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - name: Імпорт змінених сценаріїв
        run: |
          for file in $(git diff --name-only HEAD~1 HEAD -- 'workflows/*.json'); do
            [ -f "$file" ] || continue
            out=$(dotnet run --project src/OpenBP.Cli -- import "$file" --map deploy/prod-map.json)
            echo "$out"
            id=$(echo "$out" | sed -n 's/^Імпортовано: \([0-9a-f-]*\).*/\1/p')
            dotnet run --project src/OpenBP.Cli -- activate "$id"
          done
```

Старі версії на prod після цього деактивуйте або видаліть (UI чи `POST /api/v1/workflows/{id}/deactivate`): імпорт не
замінює наявний сценарій, щоб розкатка не зламала те, що вже працює.
