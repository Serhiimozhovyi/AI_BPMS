# Матриця автоматизацій

Які можливості потрібні для реальних сценаріїв компанії і чим вони закриті в OpenBP. Орієнтир — бот психологічної підтримки, звіти, БП продажів (PRD §5b). Паритет з n8n не є метою: можливість без попиту позначається «не плануємо».

Стан: **є** — працює і має приклад та тест; **частково** — є лише частина, у дужках що бракує; **немає** — відсутнє, у дужках задача беклогу або «потрібне рішення власника».

Посилання на приклад — файл у `examples/` або у `connectors/*`; тест — файл у `tests/` або `connectors/*/tests`.

## 1. Шедулери

| Можливість | Чим робиться в OpenBP | Стан | Приклад і тест |
|---|---|---|---|
| Розклад cron | `core.schedule { cron, timezone }` | є | `examples/daily-report.json`, `examples/psy-support-scheduler.json`; `tests/OpenBP.Nodes.Core.Tests/ScheduleNodeTests.cs` |
| Часовий пояс сценарію | `timezone` у `core.schedule`, `settings.timezone` сценарію, час у `core.code` | є | `examples/daily-report.json`; `ScheduleNodeTests.cs` (`Test_run_emits_current_time_in_node_timezone`) |
| Пауза на N одиниць або до дати | `core.wait` (`mode: duration`, F5-02) | є | `examples/kit-approval-business.json`; `tests/OpenBP.Worker.Tests/LongWaitTests.cs` |
| Довге очікування (дні, тижні) з пробудженням після рестарту | таймер у воркері, відновлення виконання | є | `tests/OpenBP.Worker.Tests/LongWaitTests.cs`, `ExecutionResumeTests.cs` |
| Робочі дні (пн–пт) у паузах і строках | немає в `core.wait` і `core.schedule` | немає (F7-02: `due` з `businessDays`) |  — |
| Очікування умови з опитуванням | `mcp.waitFor` | частково (приклад у `examples/` відсутній) | `tests/OpenBP.Worker.Tests/WaitForTests.cs`, `tests/OpenBP.Mcp.Tests/McpWaitForNodeTests.cs` |

## 2. Чатботи

| Можливість | Чим робиться в OpenBP | Стан | Приклад і тест |
|---|---|---|---|
| Вхід повідомлень Telegram | `core.webhook` + конектор `connectors/telegram` | є | `examples/telegram-1c.json`; `tests/OpenBP.Nodes.Core.Tests/WebhookNodeTests.cs`, `connectors/telegram/tests/OpenBP.Connectors.Telegram.Tests/TelegramConnectorTests.cs` |
| Клавіатури, редагування повідомлень, callback-кнопки | `reply_markup`, `edit_message_text`, `answer_callback_query` (F3-27) | є | `connectors/telegram/tests/OpenBP.Connectors.Telegram.Tests/TelegramConnectorTests.cs` (тести клавіатур); `connectors/psy-support/src/OpenBP.Connectors.PsySupport/Domain/Keyboard.cs` |
| Стан діалогу між повідомленнями | таблиці/БД бота, приклад — бот психологічної підтримки | є | `examples/psy-support-bot.json`; `tests/OpenBP.Cli.Tests/PsySupportBotTests.cs`, `connectors/psy-support/tests` |
| Відповідь LLM (локальна або хмарна модель) | `ai.llm` (Ollama, OpenAI, Anthropic; F3-01, F3-02) | є | `examples/telegram-1c-ollama.json`, `examples/credentials.ollama.json`; `tests/OpenBP.Ai.Tests` |
| Ескалація людині з задачею і відповіддю | сповіщення адміністратору (F3-40) є; задача людині `core.approval` (F6-02) не готова | частково (F6-02) | `examples/psy-support-alerts.json`; `tests/OpenBP.Worker.Tests/ErrorWorkflowTests.cs` |
| Швидка відповідь вебхука, дедуплікація повторних апдейтів | `core.webhook` без `respond`/дедуплікації | немає (F6-05) | — |

## 3. Події

| Можливість | Чим робиться в OpenBP | Стан | Приклад і тест |
|---|---|---|---|
| Вебхук як тригер | `core.webhook` з секретом у заголовку | є | `examples/telegram-1c.json`; `tests/OpenBP.Nodes.Core.Tests/WebhookNodeTests.cs` |
| Опитування (polling) конектора за розкладом | `mcp.poll` (F1-09, F2-12) | є | `examples/kit-approval-business.json`; `tests/OpenBP.Mcp.Tests/McpPollNodeTests.cs`, `tests/OpenBP.Worker.Tests/PollJobTests.cs` |
| Дедуплікація при polling (запам'ятати вже оброблені записи) | немає власного курсора чи дедуплікації для `mcp.poll` | немає (задачі в беклозі немає, потрібне рішення власника) | — |
| Push від конекторів (події без опитування) | тригери подій (F6-13) | немає (F6-13) | — |
| Кореляція подій з виконанням | ключ кореляції (F5-04) | частково (приклад у `examples/` відсутній) | `tests/OpenBP.Api.Tests/CorrelationKeyTests.cs` |

## 4. Обробка даних

| Можливість | Чим робиться в OpenBP | Стан | Приклад і тест |
|---|---|---|---|
| Присвоєння полів | `core.set` | є | `examples/hello-manual.json`; `tests/OpenBP.Nodes.Core.Tests/SetNodeTests.cs` |
| Код (JavaScript, Jint) | `core.code` | є | `examples/order-total.json`; `tests/OpenBP.Nodes.Core.Tests/CodeNodeTests.cs` |
| Умови і розгалуження | `core.if`, `core.switch` (більше 4 правил, F3-29) | є | `examples/kit-approval-business.json`, `examples/psy-support-scheduler.json`; `tests/OpenBP.Nodes.Core.Tests/IfNodeTests.cs`, `SwitchNodeTests.cs` |
| Цикли по списку | `core.loop` | є | `examples/order-total.json`; `tests/OpenBP.Nodes.Core.Tests/LoopNodeTests.cs` |
| Злиття гілок | `core.merge` | частково (приклад у `examples/` відсутній) | `tests/OpenBP.Nodes.Core.Tests/MergeNodeTests.cs` |
| Таблиця рішень | `core.decisionTable` (F6-19) | частково (приклад лише в `docs/workflow-format.md`) | `tests/OpenBP.Nodes.Core.Tests/DecisionTableNodeTests.cs` |
| Таблиці даних | `core.dataTable` (F6-11) | частково (приклад у `examples/` відсутній) | `tests/OpenBP.Nodes.Core.Tests/DataTableNodeTests.cs` |
| Файли: локальна папка і S3 | сховище бінарних даних (F3-19, F6-18) | є | `tests/OpenBP.Worker.Tests/LocalBinaryStoreTests.cs`, `S3BinaryStoreTests.cs` |
| Бінарні дані у виразах (`$binary`) | F3-23 | є | `tests/OpenBP.Expressions.Tests/JintExpressionEvaluatorTests.cs` |

## 5. Інтеграції

| Можливість | Чим робиться в OpenBP | Стан | Приклад і тест |
|---|---|---|---|
| Виклик інструментів MCP-серверів | `mcp.tool`, реєстр серверів (F1-08, F1-09) | є | `examples/servers.json`, `examples/daily-report.json`; `tests/OpenBP.Mcp.Tests/McpToolNodeTests.cs`, `McpServerRegistryTests.cs` |
| Генерація інструментів з OpenAPI | Connector Kit (F3-11) | є | `tools/connector-template/tests/Connector.Template.Tests/OpenApi/OpenApiGeneratorTests.cs` |
| Генерація інструментів з OData `$metadata` | Connector Kit (F3-12) | є | `tools/connector-template/tests/Connector.Template.Tests/OData/ODataGeneratorTests.cs`; приклад конектора `connectors/kit-crm` |
| Конектори Telegram, 1С (OData), Kit CRM | F3-13, F3-14, F5-05 | є | `examples/telegram-1c.json`; `connectors/telegram/tests`, `connectors/kit-crm/tests` |
| Credentials (ключі, секрети у шифрованому вигляді) | F2-04, F4-01 | є | `tests/OpenBP.Data.Tests/Credentials/ScrubbedExecutionStoreTests.cs`; `examples/credentials.ollama.json` |
| OAuth2 authorization code | F4-04 | є | `tests/OpenBP.Api.Tests/OAuth2Tests.cs` |
| Обробка помилок сценарію (error workflow) | `core.errorTrigger` (F4-06) | є | `examples/psy-support-alerts.json`; `tests/OpenBP.Worker.Tests/ErrorWorkflowTests.cs` |

## Що відкрито

- Немає в беклозі або з потрібним рішенням власника: робочі дні в розкладах (крім `due` у F7-02), дедуплікація `mcp.poll`.
- Частково, бракує прикладу в `examples/`: `mcp.waitFor`, кореляція (F5-04), `core.merge`, `core.decisionTable`, `core.dataTable`. Тести є, приклади можна додати окремою задачею.
- Нові задачі для «немає» з попитом заводяться окремим PR за рішенням власника, цей документ їх не створює.
