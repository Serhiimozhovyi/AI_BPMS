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
public sealed record CredentialFieldInfo(string Name, string Title, CredentialFieldKind Kind, bool Required);

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

    /// <summary>Усі типи в порядку показу.</summary>
    public static IReadOnlyList<CredentialTypeInfo> All { get; } =
    [
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

    private static string? Check(CredentialFieldInfo field, JsonNode? value)
    {
        if (value is null)
        {
            return field.Required ? "Обов'язкове поле." : null;
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
