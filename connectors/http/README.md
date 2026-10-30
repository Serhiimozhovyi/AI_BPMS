# Конектор HTTP і RSS для AI_BPMS

MCP-сервер, зроблений на шаблоні Connector Kit (`tools/connector-template`). Дає сценаріям дві речі, яких
бракує для перенесення сценаріїв з n8n: читання RSS/Atom і довільний HTTP-запит.

| Tool | Аргументи | Результат |
|---|---|---|
| `rss_read` | `url`, `limit?` (1–500, типово 50) | `{ feedTitle, feedLink, items: [{ title, link, isoDate, pubDate, contentSnippet, content, creator, categories, guid }] }` — поля як у вузла n8n «RSS Read»; підтримано RSS 2.0, RSS 1.0 і Atom |
| `http_request` | `url`, `method?` (GET, POST, PUT, PATCH, DELETE, HEAD), `headers?`, `query?`, `json?` **або** `body?`, `timeoutSec?` (1–300), `fail_on_status_error?` | `{ status, ok, reason, url, headers, contentType }` + тіло: `json` (JSON), `text` (текст) або `base64` (двійкові дані) |

У вузлі `mcp.tool` для `rss_read` задайте `itemsPath: "items"` — тоді кожен запис стрічки стане окремим елементом.

Статус не 2xx — помилка (`isError`) з кодом і початком відповіді; `fail_on_status_error: false` повертає статус і тіло
як звичайний результат.

## Безпека (захист від SSRF)

- лише `http` і `https`, без логіна й пароля в адресі;
- приватні адреси заборонені: loopback, RFC 1918, link-local (у тому числі метадані хмари `169.254.169.254`),
  CGNAT, ULA, multicast. Перевірка робиться на самому підключенні до вже розв'язаної IP-адреси, тому підміна DNS між
  перевіркою й запитом не допомагає; редіректи (до 5) обробляються вручну й перевіряються так само;
- розмір відповіді обмежений (`Connector:MaxResponseBytes`, типово 20 МБ), загальний таймаут запиту — `timeoutSec`;
- параметри адреси (часто це ключі API) не потрапляють ні у відповіді, ні в помилки, ні в журнали.

Налаштування (поле `env` credential конектора в AI_BPMS, секретів тут немає):

- `HTTP_ALLOWED_HOSTS` — дозволені хости через кому (`example.com`, `*.example.com`); порожньо — будь-які публічні;
- `HTTP_ALLOW_PRIVATE` — хости, яким дозволено приватні адреси (наприклад `host.docker.internal` для локального
  Ollama чи FLUX), або `*` — усім. Типово порожньо.

## Зібрати й перевірити

```
cd connectors/http
dotnet build -warnaserror
dotnet test
```

Запуск: `dotnet run --project src/OpenBP.Connectors.Http` (stdio) або
`dotnet run --project src/OpenBP.Connectors.Http -- --http http://0.0.0.0:8080` (Streamable HTTP, ендпойнт `/mcp`).
