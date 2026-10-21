# ADR-002. MCP як ядро інтеграцій і як інтерфейс для ШІ

Статус: **прийнято** · Дата: 2026-10-21

## Контекст

n8n, Make, Activepieces тримають сотні власноруч написаних інтеграцій. Для команди з однієї
людини це недосяжно і, головне, непотрібно: Model Context Protocol (MCP) став стандартом, за яким
системи описують свої дії (tools) і дані (resources) у машиночитному вигляді з JSON-схемами.
Для Telegram, Jira, Figma, GitHub, Microsoft 365, баз даних уже є MCP-сервери; для 1С, CRM чи
внутрішніх систем MCP-сервер пишеться за день поверх їхнього HTTP/OData.

Водночас користувачі OpenBP працюватимуть з Claude/ChatGPT як з колегою і очікуватимуть, що ШІ
може сам зібрати або полагодити сценарій.

## Рішення

MCP використовується у **двох напрямках**, і обидва — частина MVP, а не «фаза 8».

### 1. Вниз: OpenBP як MCP-клієнт (Universal MCP node)

- Адміністратор реєструє MCP-сервер (stdio або Streamable HTTP, з credentials) у розділі «Конектори».
- OpenBP викликає `tools/list`, кешує список tools і їхні `inputSchema`.
- У палітрі канвасу кожен tool з'являється як вузол типу `mcp.tool` з параметрами
  `{ server, tool, arguments }`. Форма налаштувань генерується з `inputSchema`.
- При виконанні вузол викликає `tools/call`; результат (content / structuredContent) стає items.
- Власні вузли (`core.*`) лишаються тільки для логіки (IF, Switch, Merge, Loop, Set, Code, Wait)
  і тригерів (webhook, schedule, manual). **Інтеграційних вузлів у коді OpenBP немає.**
- Тригери «від системи» (нове повідомлення в Telegram, новий лист) — через MCP-ресурси з
  підпискою, або через polling-обгортку над tool, або через webhook зовнішньої системи у `core.webhook`.
  Для MVP достатньо webhook + polling.

### 2. Вгору: OpenBP як MCP-сервер

Endpoint `/mcp` (Streamable HTTP, авторизація API-ключем або OAuth) віддає tools:

| Tool | Призначення |
|---|---|
| `list_workflows`, `get_workflow` | читання сценаріїв у форматі `workflow-format.md` |
| `create_workflow`, `update_workflow` | запис з повною валідацією (та сама, що в UI) |
| `validate_workflow` | перевірка без збереження — щоб агент ітерував |
| `run_workflow`, `get_execution`, `list_executions` | запуск і діагностика |
| `describe_connectors` | які MCP-сервери і tools доступні — агент знає, з чого збирати |
| `test_node` | виконати один вузол на тестових даних |

Resources: `openbp://workflows/{id}`, `openbp://executions/{id}`, `openbp://schema/workflow-format`.

Так Claude Desktop, Claude Code, ChatGPT чи власний агент компанії підключають OpenBP як
звичайний MCP-сервер і будують процеси самі.

### 3. Connector Kit

Шаблон репозиторію `openbp-connector-template` (.NET, `ModelContextProtocol` SDK) для написання
MCP-сервера над REST/OData за годину: конфіг базової адреси й авторизації, генерація tools із
OpenAPI/OData `$metadata`, тести. Перші конектори, які ми робимо самі: **1С (OData)**, **Telegram
Bot API**, **пошта (Microsoft Graph / IMAP)**. Усе інше — готові сервери спільноти.

## Наслідки

- `node-sdk.md` замінено на `connectors.md`: контракт `core.*`-вузлів і `mcp.tool`.
- `workflow-format.md`: вузол `mcp.tool`, секція `mcpServers` у налаштуваннях сценарію не потрібна —
  сервер посилається за id, як credentials.
- Фаза 1 roadmap містить MCP-клієнт; фаза 3 — MCP-сервер і Connector Kit.
- Ризик: якість сторонніх MCP-серверів нерівна. Пом'якшення: таймаути, retry, `continueOnFail`,
  і Connector Kit, щоб замінити поганий сервер власним.
- Ризик: протокол розвивається. Пом'якшення: вся взаємодія — через офіційний SDK, оновлення версії SDK — окрема задача в беклозі раз на місяць.
