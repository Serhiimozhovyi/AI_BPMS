using System.Text.Json.Nodes;
using OpenBP.Application.Errors;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Credentials;

/// <summary>Вид значення поля credential'а.</summary>
public enum CredentialFieldKind
{
    /// <summary>Рядок (несекретний: адреса, ім'я користувача).</summary>
    Text = 0,

    /// <summary>Секретний рядок (ключ, пароль, токен).</summary>
    Secret = 1,

    /// <summary>Абсолютна http(s)-адреса.</summary>
    Url = 2,

    /// <summary>Об'єкт «назва → секретне значення» (заголовки, змінні середовища).</summary>
    SecretMap = 3,

    /// <summary>Керує OpenBP (токени OAuth2): користувач не задає, UI не показує.</summary>
    Managed = 4,
}

/// <summary>Поле типу credential'а.</summary>
/// <param name="Name">Ключ у даних.</param>
/// <param name="Title">Підпис для UI.</param>
/// <param name="Kind">Вид значення.</param>
/// <param name="Required">Обов'язкове.</param>
/// <param name="Description">Підказка під полем (F3-93).</param>
/// <param name="Placeholder">Приклад значення для порожнього поля (F3-93).</param>
/// <param name="Options">Допустимі значення (вибір зі списку); <c>null</c> — довільний рядок.</param>
public sealed record CredentialFieldInfo(
    string Name,
    string Title,
    CredentialFieldKind Kind,
    bool Required,
    string? Description = null,
    string? Placeholder = null,
    IReadOnlyList<string>? Options = null);

/// <summary>Тип credential'а (<c>GET /api/v1/credentials/types</c>).</summary>
/// <param name="Type">Значення <c>credentials.type</c>.</param>
/// <param name="DisplayName">Назва для UI.</param>
/// <param name="Description">Де використовується.</param>
/// <param name="Fields">Поля; для <see cref="CredentialTypes.Generic"/> — порожньо (довільні пари).</param>
/// <param name="Schema">JSON Schema даних (секретні поля мають <c>"writeOnly": true</c>).</param>
public sealed record CredentialTypeInfo(
    string Type,
    string DisplayName,
    string Description,
    IReadOnlyList<CredentialFieldInfo> Fields,
    JsonObject Schema);

/// <summary>
/// Каталог типів credentials і перевірка даних за ним. Тип, якого немає в каталозі (створений до F4-01 напряму в БД),
/// перевіряється як <see cref="Generic"/>.
/// </summary>
public static class CredentialTypes
{
    /// <summary>Заголовки HTTP-конектора: <c>{ "headers": { … } }</c>.</summary>
    public const string McpHttp = "mcp.http";

    /// <summary>Змінні середовища stdio-конектора: <c>{ "env": { … } }</c>.</summary>
    public const string McpStdio = "mcp.stdio";

    /// <summary>Довільні пари «назва → секрет».</summary>
    public const string Generic = "generic";

    /// <summary>Токен бота Telegram (F3-93).</summary>
    public const string TelegramBot = "telegram.bot";

    /// <summary>API-ключ у заголовку чи параметрі запиту.</summary>
    public const string ApiKey = "apiKey";

    /// <summary>Bearer-токен.</summary>
    public const string Bearer = "bearer";

    /// <summary>Користувач і пароль (HTTP Basic).</summary>
    public const string Basic = "basic";

    /// <summary>OAuth2 client credentials: токен отримує й оновлює OpenBP.</summary>
    public const string OAuth2ClientCredentials = OAuth2Credential.ClientCredentialsType;

    /// <summary>Підключення до бази даних.</summary>
    public const string Database = "database";

    /// <summary>Сервісний акаунт Google (JSON-ключ).</summary>
    public const string GoogleServiceAccount = "google.serviceAccount";

    /// <summary>Рушії бази даних типу <see cref="Database"/>.</summary>
    public static IReadOnlyList<string> DatabaseEngines { get; } = ["postgres", "mysql"];

    /// <summary>Режими SSL типу <see cref="Database"/>.</summary>
    public static IReadOnlyList<string> SslModes { get; } = ["disable", "prefer", "require"];

    /// <summary>Усі типи в порядку показу.</summary>
    public static IReadOnlyList<CredentialTypeInfo> All { get; } =
    [
        Build(TelegramBot, "Telegram: бот", "Бот Telegram для конектора Telegram.",
            [new("token", "Токен бота", CredentialFieldKind.Secret, true, "Видає @BotFather після команди /newbot.", "123456789:AAE…")]),
        Build(ApiKey, "API-ключ", "Ключ сервісу в заголовку або параметрі запиту.",
            [
                new("apiKey", "API-ключ", CredentialFieldKind.Secret, true),
                new("name", "Назва заголовка чи параметра", CredentialFieldKind.Text, false, "Як сервіс очікує ключ; порожньо — X-API-Key.", "X-API-Key"),
                new("in", "Де передавати", CredentialFieldKind.Text, false, "header — у заголовку (типово), query — у параметрі адреси.", null, ["header", "query"]),
            ]),
        Build(Bearer, "Bearer-токен", "Токен у заголовку Authorization: Bearer ….",
            [new("token", "Токен", CredentialFieldKind.Secret, true, "Персональний токен доступу або API-токен сервісу.")]),
        Build(Basic, "Користувач і пароль", "Вхід за ім'ям користувача й паролем (HTTP Basic).",
            [
                new("username", "Користувач", CredentialFieldKind.Text, true),
                new("password", "Пароль", CredentialFieldKind.Secret, false),
            ]),
        Build(OAuth2ClientCredentials, "OAuth2: сервісний клієнт (client credentials)", "Доступ сервісу до сервісу без входу користувача (Keycloak, Entra ID…): OpenBP сам отримує й оновлює токен.",
            [
                new("tokenUrl", "Адреса токена", CredentialFieldKind.Url, true, "Token endpoint провайдера.", "https://sso.example.com/realms/main/protocol/openid-connect/token"),
                new("clientId", "Client ID", CredentialFieldKind.Text, true, "Ідентифікатор сервісного клієнта."),
                new("clientSecret", "Client secret", CredentialFieldKind.Secret, true, "Секрет сервісного клієнта."),
                new("scopes", "Scopes (через пробіл)", CredentialFieldKind.Text, false, "Лише якщо провайдер їх вимагає.", "api.read api.write"),
                new("audience", "Audience", CredentialFieldKind.Text, false, "Лише якщо провайдер його вимагає."),
                .. OAuth2Credential.ClientCredentialsManagedFields.Select(f => new CredentialFieldInfo(f, f, CredentialFieldKind.Managed, false)),
            ]),
        Build(Database, "База даних", "Підключення до PostgreSQL або MySQL.",
            [
                new("engine", "Рушій", CredentialFieldKind.Text, true, null, null, DatabaseEngines),
                new("host", "Хост", CredentialFieldKind.Text, true, "Адреса сервера бази.", "db.example.com"),
                new("port", "Порт", CredentialFieldKind.Text, false, "Порожньо — типовий (5432 для PostgreSQL, 3306 для MySQL).", "5432"),
                new("database", "База", CredentialFieldKind.Text, true, null, "crm"),
                new("username", "Користувач", CredentialFieldKind.Text, true, "Краще окремий користувач лише з потрібними правами.", "openbp_reader"),
                new("password", "Пароль", CredentialFieldKind.Secret, false),
                new("ssl", "SSL", CredentialFieldKind.Text, false, "disable — без шифрування, prefer — якщо сервер уміє, require — обов'язково.", null, SslModes),
            ]),
        Build(GoogleServiceAccount, "Google: сервісний акаунт", "JSON-ключ сервісного акаунта Google Cloud (Таблиці, Диск…).",
            [new("serviceAccountJson", "JSON-ключ", CredentialFieldKind.Secret, true, "Вміст завантаженого файлу ключа повністю; таблицю відкрийте для client_email з ключа.", "{ \"type\": \"service_account\", … }")]),
        Build(McpHttp, "Конектор HTTP: заголовки", "Секретні заголовки MCP-сервера (напр. Authorization).",
            [new("headers", "Заголовки", CredentialFieldKind.SecretMap, true)]),
        Build(McpStdio, "Конектор stdio: змінні середовища", "Секретні змінні середовища MCP-сервера (токени, паролі).",
            [new("env", "Змінні середовища", CredentialFieldKind.SecretMap, true)]),
        Build("openai-compatible", "LLM: OpenAI-сумісний (Ollama, vLLM…)", "Вузол ai.llm з провайдером openai-compatible.",
            [new("baseUrl", "Адреса API", CredentialFieldKind.Url, true), new("apiKey", "API-ключ", CredentialFieldKind.Secret, false)]),
        Build("anthropic", "LLM: Anthropic", "Вузол ai.llm з провайдером anthropic.",
            [new("apiKey", "API-ключ", CredentialFieldKind.Secret, true)]),
        Build("openai", "LLM: OpenAI", "Вузол ai.llm з провайдером openai.",
            [new("apiKey", "API-ключ", CredentialFieldKind.Secret, true)]),
        Build(OAuth2Credential.Type, "OAuth2 (authorization code)", "Вхід через провайдера (Microsoft, Google…): кнопка «Підключити» отримує токен, OpenBP оновлює його сам. Для HTTP-конектора додає Authorization: Bearer.",
            [
                new("authorizationUrl", "Адреса авторизації", CredentialFieldKind.Url, true),
                new("tokenUrl", "Адреса токена", CredentialFieldKind.Url, true),
                new("clientId", "Client ID", CredentialFieldKind.Text, true),
                new("clientSecret", "Client secret", CredentialFieldKind.Secret, false),
                new("scopes", "Scopes (через пробіл)", CredentialFieldKind.Text, false),
                new("audience", "Audience", CredentialFieldKind.Text, false),
                .. OAuth2Credential.ManagedFields.Select(f => new CredentialFieldInfo(f, f, CredentialFieldKind.Managed, false)),
            ]),
        Build("webhook.header", "Webhook: токен у заголовку", "Тригер core.webhook з auth.type = header.",
            [new("token", "Токен", CredentialFieldKind.Secret, true)]),
        Build("webhook.basic", "Webhook: Basic", "Тригер core.webhook з auth.type = basic.",
            [new("username", "Користувач", CredentialFieldKind.Text, true), new("password", "Пароль", CredentialFieldKind.Secret, true)]),
        Build("form.token", "Форма: токен посилання", "Тригер core.formTrigger з auth = token: посилання /form/{path}?token=….",
            [new("token", "Токен", CredentialFieldKind.Secret, true)]),
        Build(Generic, "Інше: довільні пари", "Будь-які секрети «назва → значення».", []),
    ];

    /// <summary>Тип за назвою або <c>null</c>.</summary>
    /// <param name="type">Значення <c>credentials.type</c>.</param>
    public static CredentialTypeInfo? Find(string type) => All.FirstOrDefault(t => t.Type == type);

    /// <summary>Перевіряє дані за типом.</summary>
    /// <param name="type">Тип; невідомий — як <see cref="Generic"/>.</param>
    /// <param name="data">Дані.</param>
    /// <exception cref="CredentialValidationException">Дані не відповідають типу.</exception>
    public static void Validate(string type, JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var info = Find(type) ?? Find(Generic)!;
        var errors = new List<CredentialFieldError>();
        if (info.Fields.Count == 0)
        {
            foreach (var (key, value) in data)
            {
                if (!IsText(value))
                {
                    errors.Add(new(key, "Очікується рядок."));
                }
            }

            if (data.Count == 0)
            {
                errors.Add(new("data", "Потрібна хоча б одна пара «назва → значення»."));
            }
        }
        else
        {
            foreach (var field in info.Fields)
            {
                var error = Check(field, data[field.Name]);
                if (error is not null)
                {
                    errors.Add(new(field.Name, error));
                }
            }

            errors.AddRange(data.Select(p => p.Key)
                .Where(k => info.Fields.All(f => f.Name != k))
                .Select(k => new CredentialFieldError(k, "Невідоме поле для цього типу.")));
            if (errors.Count == 0)
            {
                errors.AddRange(CheckType(info.Type, data));
            }
        }

        if (errors.Count > 0)
        {
            throw new CredentialValidationException(errors);
        }
    }

    /// <summary>
    /// Шляхи заповнених значень без самих значень: <c>apiKey</c>, <c>headers.Authorization</c>. Назви ключів
    /// заголовків і змінних не секретні; значення не повертаються ніколи.
    /// </summary>
    /// <param name="data">Розшифровані дані.</param>
    public static IReadOnlyList<string> FilledFields(JsonObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var result = new List<string>();
        foreach (var (key, value) in data)
        {
            if (value is JsonObject map)
            {
                result.AddRange(map.Where(p => p.Value is not null).Select(p => $"{key}.{p.Key}"));
            }
            else if (value is not null)
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>
    /// Дані для <c>SecretScrubber</c> без полів-перемикачів загальних типів (<c>engine</c>, <c>ssl</c>, <c>in</c>) і порту:
    /// їхні значення (<c>postgres</c>, <c>require</c>, <c>5432</c>) не секретні, а маскування зіпсувало б будь-який текст кроків.
    /// </summary>
    /// <param name="data">Розшифровані дані.</param>
    public static JsonObject? ForScrubber(JsonObject? data)
    {
        if (data is null)
        {
            return null;
        }

        var result = (JsonObject)data.DeepClone();
        foreach (var name in PlainFields)
        {
            result.Remove(name);
        }

        return result;
    }

    private static readonly string[] PlainFields = ["engine", "ssl", "in", "port"];

    /// <summary>
    /// Часткова заміна: ключ із <paramref name="patch"/> замінює значення, <c>null</c> — видаляє; для об'єктів
    /// (заголовки, змінні) так само на рівні їхніх ключів. Ключі, яких у запиті немає, лишаються.
    /// </summary>
    /// <param name="current">Поточні дані (не змінюються).</param>
    /// <param name="patch">Зміни.</param>
    public static JsonObject Merge(JsonObject current, JsonObject patch)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(patch);
        var result = (JsonObject)current.DeepClone();
        foreach (var (key, value) in patch)
        {
            if (value is null)
            {
                result.Remove(key);
            }
            else if (value is JsonObject map && result[key] is JsonObject existing)
            {
                foreach (var (k, v) in map)
                {
                    if (v is null)
                    {
                        existing.Remove(k);
                    }
                    else
                    {
                        existing[k] = v.DeepClone();
                    }
                }
            }
            else
            {
                result[key] = value.DeepClone();
            }
        }

        return result;
    }

    /// <summary>Перевірки типу поверх полів: значення, що мають бути числом чи JSON.</summary>
    private static IEnumerable<CredentialFieldError> CheckType(string type, JsonObject data)
    {
        if (type == Database && OAuth2Credential.Text(data, "port") is { Length: > 0 } port
            && !(int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && number is > 0 and < 65536))
        {
            yield return new("port", "Очікується номер порту від 1 до 65535.");
        }

        if (type == GoogleServiceAccount && OAuth2Credential.Text(data, "serviceAccountJson") is { } json)
        {
            JsonObject? key;
            try
            {
                key = JsonNode.Parse(json) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                key = null;
            }

            // Без вмісту ключа в повідомленні: лише що саме не так.
            if (key is null)
            {
                yield return new("serviceAccountJson", "Очікується вміст JSON-файлу ключа.");
            }
            else if (OAuth2Credential.Text(key, "client_email") is not { Length: > 0 } || OAuth2Credential.Text(key, "private_key") is not { Length: > 0 })
            {
                yield return new("serviceAccountJson", "У ключі немає client_email або private_key: завантажте ключ сервісного акаунта ще раз.");
            }
        }
    }

    private static string? Check(CredentialFieldInfo field, JsonNode? value)
    {
        if (value is null)
        {
            return field.Required ? "Обов'язкове поле." : null;
        }

        if (field.Options is { } options)
        {
            if (!IsText(value))
            {
                return "Очікується рядок.";
            }

            var text = value.GetValue<string>();
            return options.Contains(text) || (!field.Required && text.Length == 0) ? null : $"Допустимі значення: {string.Join(", ", options)}.";
        }

        switch (field.Kind)
        {
            case CredentialFieldKind.SecretMap:
                if (value is not JsonObject map)
                {
                    return "Очікується об'єкт «назва → значення».";
                }

                if (field.Required && map.Count == 0)
                {
                    return "Потрібна хоча б одна пара «назва → значення».";
                }

                return map.Any(p => p.Key.Length == 0 || !IsText(p.Value)) ? "Кожна назва непорожня, кожне значення — рядок." : null;
            case CredentialFieldKind.Managed:
                return value is JsonValue ? null : "Очікується рядок.";
            case CredentialFieldKind.Url:
                return IsText(value) && Uri.TryCreate(value.GetValue<string>(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    ? null
                    : "Очікується абсолютна http(s)-адреса.";
            default:
                if (!IsText(value))
                {
                    return "Очікується рядок.";
                }

                return field.Required && value.GetValue<string>().Length == 0 ? "Обов'язкове поле." : null;
        }
    }

    private static bool IsText(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out _);

    private static CredentialTypeInfo Build(string type, string displayName, string description, IReadOnlyList<CredentialFieldInfo> fields)
    {
        var properties = new JsonObject();
        foreach (var f in fields.Where(f => f.Kind != CredentialFieldKind.Managed))
        {
            JsonObject schema = f.Kind switch
            {
                CredentialFieldKind.SecretMap => new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = new JsonObject { ["type"] = "string", ["writeOnly"] = true },
                },
                CredentialFieldKind.Url => new JsonObject { ["type"] = "string", ["format"] = "uri" },
                CredentialFieldKind.Secret => new JsonObject { ["type"] = "string", ["writeOnly"] = true },
                _ => new JsonObject { ["type"] = "string" },
            };
            schema["title"] = f.Title;
            if (f.Description is not null)
            {
                schema["description"] = f.Description;
            }

            if (f.Placeholder is not null)
            {
                schema["examples"] = new JsonArray(f.Placeholder);
            }

            if (f.Options is not null)
            {
                schema["enum"] = new JsonArray([.. f.Options.Select(o => (JsonNode)JsonValue.Create(o))]);
            }

            properties[f.Name] = schema;
        }

        var root = new JsonObject { ["type"] = "object" };
        if (fields.Count == 0)
        {
            root["additionalProperties"] = new JsonObject { ["type"] = "string", ["writeOnly"] = true };
        }
        else
        {
            root["properties"] = properties;
            root["required"] = new JsonArray([.. fields.Where(f => f.Required).Select(f => (JsonNode)JsonValue.Create(f.Name))]);
            root["additionalProperties"] = false;
        }

        return new CredentialTypeInfo(type, displayName, description, fields, root);
    }
}
