# Підключити Claude і ChatGPT до OpenBP

OpenBP — MCP-сервер. Підключений ШІ-агент бачить ваші конектори й типи вузлів, збирає сценарії зі слів, перевіряє й
запускає їх, читає журнал, пояснює процеси простими словами, приймає рішення в задачах — з тими самими правами, що й
користувач, чий ключ він використовує. Окремого ключа Anthropic чи OpenAI в самому OpenBP для цього не потрібно:
модель працює на боці Claude чи ChatGPT. Повний довідник tools і resources — [docs/mcp-server.md](../mcp-server.md).

Коротко, що де працює:

| Клієнт | Як підключити | OpenBP на `localhost` | Статус |
|---|---|---|---|
| Claude Code (CLI, IDE) | `claude mcp add` з заголовком | так | працює |
| Claude Desktop | міст `mcp-remote` у `claude_desktop_config.json` | так | працює |
| claude.ai (веб, Desktop, мобільний) — «Custom connector» | URL + «Request headers» | ні, потрібна публічна HTTPS-адреса | лише якщо вашій організації відкрито бета «Request headers» |
| Codex (CLI, IDE) від OpenAI | `config.toml` з `bearer_token_env_var` | так | працює |
| OpenAI API (Responses, tool `mcp`) | `server_url` + `authorization` | ні, потрібна публічна HTTPS-адреса | працює |
| ChatGPT (веб, Desktop) — Developer mode | — | — | **поки не працює**: ChatGPT уміє лише OAuth або «без автентифікації», а `/mcp` OpenBP приймає тільки API-ключ (див. [§4.1](#41-chatgpt-веб-і-desktop)) |

## 1. Що дає підключення

| Що агент робить | Tools |
|---|---|
| Дізнається, що є | `describe_connectors` (системи й їхні tools), `describe_nodes` (типи вузлів і параметри), resource `openbp://schema/workflow-format` |
| Шукає й читає сценарії | `list_workflows`, `get_workflow`, `list_workflow_groups`, `list_tables`, `query_table` |
| Збирає й змінює | `validate_workflow`, `create_workflow`, `update_workflow`, `publish_workflow`, `create_workflow_group`, `move_workflow` |
| Тестує | `run_workflow` (із тестовими `startItems`), `test_node`, `list_evaluations`, `create_evaluation`, `run_evaluation` |
| Розбирається з виконаннями | `list_executions`, `get_execution`, `cancel_execution`, `resume_execution`, `list_violations`, `get_execution_stages`, `act_on_stage`, `override_rule` |
| Пояснює процес | `get_workflow_explanation`, `save_workflow_explanation` — текст картки «Пояснення процесу» в редакторі |
| Задачі погодження | `list_tasks`, `get_task`, `complete_task` |

Чого агент **не** може: вмикати й вимикати сценарії (перемикач «Активний» — лише людина в UI), керувати конекторами,
обліковими даними й користувачами, бачити значення секретів (у відповіді вони не потрапляють ніколи).

## 2. Підготовка (один раз)

1. **Окремий користувач для агента.** Попросіть адміністратора завести користувача з роллю `editor` (наприклад,
   `ai-agent@firma.ua`): його ключ можна відкликати, не чіпаючи людей, а в історії версій видно, що зміну зробив агент.
   Для лише читання й пояснень досить `viewer`, але тоді агент не збереже пояснення й не створить сценарій.
2. **API-ключ.** Увійдіть цим користувачем → «Налаштування» → «API-ключі» → назва (наприклад, `claude-desktop`) →
   за потреби обмежте ключ одним проєктом замість «Увесь інстанс» → «Створити ключ». Ключ `obp_…` показується **один
   раз**: скопіюйте його в менеджер паролів. На кожен клієнт — свій ключ.
3. **Адреса MCP** — блок «Підключити ШІ-агента» на тій самій сторінці: `<адреса OpenBP>/mcp`, наприклад
   `https://openbp.example.com/mcp`. Там же готовий JSON-фрагмент із ключем для клієнтів, що розуміють `type: http`.
4. **Перевірка без агента:**

   ```bash
   curl -sS -X POST https://openbp.example.com/mcp \
     -H "Authorization: Bearer obp_ваш_ключ" \
     -H 'content-type: application/json' -H 'accept: application/json, text/event-stream' \
     -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
   # → event: message / data: {"result":{"tools":[{"name":"describe_connectors",…
   # 401 — немає ключа, ключ відкликано або помилка в ньому
   ```

Як автентифікує `/mcp`: лише заголовок `Authorization: Bearer obp_…` (API-ключ). Вхід паролем у браузері (cookie) і
токени Keycloak / Entra ID для `/mcp` не діють. Транспорт — Streamable HTTP без серверних сесій.

**Де стоїть OpenBP.** Хмарні клієнти (claude.ai, ChatGPT, OpenAI API) ходять до `/mcp` зі своїх серверів, тому
OpenBP має бути доступний з інтернету за HTTPS ([reverse proxy](../admin/README.md#3-https-через-reverse-proxy)). Локальний
OpenBP (`http://localhost:…`) бачать лише клієнти на тому самому комп'ютері: Claude Code, Claude Desktop через
`mcp-remote`, Codex. Не відкривайте `/mcp` назовні без HTTPS.

## 3. Claude

### 3.1 Claude Code

```bash
claude mcp add --transport http openbp https://openbp.example.com/mcp \
  --header "Authorization: Bearer obp_ваш_ключ"
claude mcp list          # openbp: … ✓ Connected
```

Для команди — у `.mcp.json` проєкту, ключ зі змінної середовища (у репозиторій не комітьте):

```json
{
  "mcpServers": {
    "openbp": {
      "type": "http",
      "url": "https://openbp.example.com/mcp",
      "headers": { "Authorization": "Bearer ${OPENBP_API_KEY}" }
    }
  }
}
```

Локальний OpenBP — та сама команда з `http://localhost:<порт>/mcp` (порт веб-інтерфейсу: `/mcp` проксується на Api).
У сесії `/mcp` показує стан сервера і його tools.

### 3.2 Claude Desktop (через `mcp-remote`)

Найпростіший спосіб для Desktop, який працює і з локальним OpenBP. Потрібен Node.js 18+ (`node -v`).

1. Claude Desktop → Settings → Developer → **Edit Config** (відкриється `claude_desktop_config.json`).
2. Додайте:

   ```json
   {
     "mcpServers": {
       "openbp": {
         "command": "npx",
         "args": [
           "-y", "mcp-remote", "https://openbp.example.com/mcp",
           "--header", "Authorization:${OPENBP_AUTH}"
         ],
         "env": { "OPENBP_AUTH": "Bearer obp_ваш_ключ" }
       }
     }
   }
   ```

   Для `http://localhost:…` додайте в `args` ще `"--allow-http"`. Ключ стоїть у `env`, а не в `args`, бо пробіл у
   `Bearer obp_…` усередині аргументу ламає запуск на Windows.
3. Повністю закрийте й знову відкрийте Claude Desktop. У чаті кнопка «+» → «Connectors» (або значок інструментів)
   покаже `openbp` з переліком tools.

### 3.3 claude.ai — custom connector (веб, Desktop, мобільний)

Так OpenBP з'являється в усіх клієнтах Claude вашого акаунта, а не лише на одному комп'ютері. Умови:

- OpenBP доступний з інтернету за HTTPS;
- у діалозі «Add custom connector» є розділ **«Request headers»** — це бета, відкрита не всім організаціям. OAuth OpenBP
  поки не підтримує, тож без «Request headers» підключити його як custom connector не вийде — користуйтеся
  [Claude Desktop](#32-claude-desktop-через-mcp-remote) або [Claude Code](#31-claude-code).

Кроки (Free, Pro, Max — сам користувач; Team і Enterprise — власник організації в «Organization settings → Connectors» →
«Add» → «Custom»):

1. «Customize → Connectors» → **«Add custom connector»**.
2. Назва — `OpenBP`, «MCP server URL» — `https://openbp.example.com/mcp`.
3. «Authentication» — **«No sign-in»**.
4. «Request headers» → `authorization`, значення — `Bearer obp_ваш_ключ` (саме з `Bearer ` і пробілом: Claude надсилає
   значення як є), «Required».
5. «Add». У чаті «+» → «Connectors» → увімкніть OpenBP для розмови.

Змінити ключ потім не можна: видаліть конектор і додайте знову. В «Customize → Connectors» можна заблокувати окремі
tools (наприклад, `cancel_execution`) або дозволити їх без підтвердження.

## 4. ChatGPT та інші продукти OpenAI

### 4.1 ChatGPT (веб і Desktop)

ChatGPT підключає власні MCP-сервери в **Developer mode** (Pro, Plus, Business, Enterprise, Education): Settings →
Apps → Advanced settings → Developer mode → «Create app». Але автентифікація там — лише **OAuth** або **«No
Authentication»**; власного заголовка з API-ключем немає. `/mcp` OpenBP приймає тільки API-ключ, тому напряму ChatGPT
до OpenBP зараз не підключається.

Не обходьте це, відкриваючи `/mcp` без автентифікації чи ставлячи проксі, що підставляє ключ за публічною адресою:
тоді будь-хто з адресою зможе змінювати й запускати ваші сценарії. Щоб ChatGPT запрацював, OpenBP має отримати OAuth для
`/mcp` (вхід через Keycloak / Entra ID чи власний сервер авторизації) — це окрема задача розробки.

До того часу ті самі можливості від OpenAI дають Codex і OpenAI API нижче.

### 4.2 Codex (CLI та розширення для IDE)

У `~/.codex/config.toml` (або `.codex/config.toml` довіреного проєкту):

```toml
[mcp_servers.openbp]
url = "https://openbp.example.com/mcp"
bearer_token_env_var = "OPENBP_API_KEY"
```

`bearer_token_env_var` — **назва** змінної середовища, Codex сам додасть `Authorization: Bearer <значення>`. Перед
запуском:

```bash
export OPENBP_API_KEY=obp_ваш_ключ
codex            # у сесії /mcp покаже openbp і його tools
```

Працює і з `http://localhost:<порт>/mcp`.

### 4.3 OpenAI API (Responses, tool `mcp`)

Для власного агента на OpenAI API; OpenBP має бути доступний з інтернету.

```json
{
  "model": "<модель>",
  "tools": [{
    "type": "mcp",
    "server_label": "openbp",
    "server_url": "https://openbp.example.com/mcp",
    "authorization": "obp_ваш_ключ",
    "require_approval": { "never": { "tool_names": ["describe_connectors", "describe_nodes", "list_workflows", "get_workflow", "validate_workflow"] } }
  }],
  "input": "Які сценарії в OpenBP зараз активні?"
}
```

OpenAI не зберігає `authorization`, його треба передавати в кожному запиті. `allowed_tools` обмежує, які tools бачить
модель.

## 4.4 Локальна модель: Ollama на вашому комп'ютері

Помічник у редакторі і вузол `ai.llm` вміють працювати з локальною Ollama без жодного ключа.

1. Установіть модель на комп'ютері з Ollama: `ollama pull qwen3:14b` (для 18 ГБ пам'яті; `qwen3:8b` — легша й швидша, але гірше справляється зі складними процесами).
2. «Підключення → Облікові дані» → тип **LLM: Ollama (локально)**. Якщо OpenBP працює в Docker, а Ollama на тому ж Mac, залиште адресу порожньою: `http://host.docker.internal:11434`. Контекст за замовчуванням — 16384 токени (більше контексту — більше пам'яті).
3. «Налаштування → Помічник»: оберіть ці облікові дані — поле «Модель» запропонує встановлені моделі зі списку.

Не обирайте тип «OpenAI-сумісний» для Ollama: він не передає розмір контексту, і Ollama мовчки обрізає запит.

## 5. Що просити в агента

Агент сам обирає tools; достатньо описати задачу. Приклади, які працюють:

| Запит | Що агент зробить |
|---|---|
| «Які конектори підключені до OpenBP і що вони вміють?» | `describe_connectors` |
| «Зроби сценарій: коли в Кіті з'являється нова угода, напиши менеджеру в Telegram, чат 42» | `describe_nodes` + `describe_connectors` → складе сценарій → `validate_workflow` (виправить помилки сам) → `create_workflow` → `run_workflow` з тестовою угодою → покаже кроки |
| «Поясни сценарій «Заявка з Telegram → 1С» і збережи пояснення в OpenBP» | `get_workflow` → `save_workflow_explanation`; текст з'явиться в картці «Пояснення процесу» |
| «Чому впало останнє виконання сценарію «Рахунки»? Виправ і перезапусти з кроку, що впав» | `list_executions` → `get_execution` → `update_workflow` → `resume_execution` |
| «Додай перед відправкою погодження керівником з коментарем» | `get_workflow` → вузол `core.approval` → `update_workflow` |
| «Розклади процес на етапи зі строками й розміть схему для бізнес-вигляду» | `update_workflow` з `stages[]` і бізнес-атрибутами |
| «Які задачі чекають мого рішення? Погодь заявку №17 з коментарем «ок»» | `list_tasks` → `get_task` → `complete_task` |
| «Опублікуй зміни» | `publish_workflow` (увімкнути «Активний» доведеться в UI) |

Поради:

- Агент зберігає чернетку: активний сценарій працює на опублікованій версії, доки не скажете «Опублікуй».
- Перед першим запуском попросіть тестовий прогін із вигаданими даними (`run_workflow` зі `startItems`), щоб не чіпати
  живі системи. Бойові боти й CRM тестуйте на тестових копіях.
- Кожна зміна агента — нова версія в «Історії» редактора, її можна переглянути й відновити.
- Пояснення процесу після змін старіє; картка покаже готову фразу для агента — просто вставте її в чат.

## 6. Безпека

- Ключ — як пароль: не вставляйте його в чат з агентом, у репозиторій чи скриншоти. Зберігайте в конфігурації клієнта
  або змінній середовища.
- Один клієнт — один ключ; непотрібний відкличте в «Налаштування» → «API-ключі» (відкликаний одразу дає 401). Там же
  видно «Останнє використання».
- Обмежуйте ключ проєктом, якщо агенту потрібні лише його сценарії.
- Tools, що змінюють дані, позначені для клієнтів як записувальні (`cancel_execution` — як руйнівний), тож Claude і Codex
  питають підтвердження. Не вмикайте «дозволити завжди» для `update_workflow`, `publish_workflow`, `complete_task`, якщо
  не впевнені.
- Агент бачить дані кроків виконань (уже без секретів). Якщо в процесах є персональні дані, враховуйте це, обираючи
  клієнт і план.

## 7. Якщо не працює

| Симптом | Що перевірити |
|---|---|
| 401 / «Unauthorized» | Ключ відкликано чи скопійовано не повністю; у заголовку має бути `Bearer obp_…` з пробілом. Перевірте `curl` з [§2](#2-підготовка-один-раз). |
| Claude Desktop не бачить `openbp` | Перезапустіть Desktop повністю; `node -v` ≥ 18; для `http://` — `--allow-http`; журнал — Settings → Developer → openbp → «Open logs». |
| claude.ai: «Couldn't connect» | Адреса недоступна з інтернету або без HTTPS; немає розділу «Request headers» — див. [§3.3](#33-claudeai--custom-connector-веб-desktop-мобільний). |
| ChatGPT просить OAuth | Очікувано, див. [§4.1](#41-chatgpt-веб-і-desktop). |
| `FORBIDDEN` у відповіді tool'а | Роль ключа замала (наприклад, `viewer` для `update_workflow`). |
| `NOT_FOUND` на сценарій, що є в UI | Ключ обмежено іншим проєктом. |
| `WORKFLOW_VERSION_CONFLICT` | Сценарій щойно змінили в редакторі; агент має перечитати `get_workflow` і повторити. |
| `WORKFLOW_INVALID` | Агент отримає список помилок з `path` і зазвичай виправить сам; попросіть «перевір і виправ». |
