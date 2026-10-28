using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBP.Application.Credentials;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Connectors;

/// <summary>
/// Відображення полів credential'а загального типу на змінні середовища (stdio) чи заголовки (http) MCP-сервера
/// (F3-93, docs/connectors.md §5.2). Ключ — поле даних credential'а (<c>clientSecret</c>) або обчислюване значення
/// (<see cref="AccessToken"/>, <see cref="Authorization"/>, <see cref="ConnectionString"/>); значення — назва змінної
/// (<c>KIT_CLIENT_SECRET</c>) або заголовок з необов'язковим префіксом (<c>Authorization: Bearer</c>). Поле, якого в
/// credential'і немає, пропускається: старі credentials <c>mcp.stdio</c>/<c>mcp.http</c> з тим самим конектором працюють як раніше.
/// Спільне для Api (<see cref="ConnectorRegistration"/>) і воркера.
/// </summary>
public static partial class ConnectorCredentialMap
{
    /// <summary>Токен OAuth2 (<c>accessToken</c>); для <c>oauth2.clientCredentials</c> OpenBP отримує його перед запуском сервера.</summary>
    public const string AccessToken = "$accessToken";

    /// <summary>Повне значення заголовка <c>Authorization</c>: Bearer за токеном, Basic за користувачем і паролем.</summary>
    public const string Authorization = "$authorization";

    /// <summary>Рядок підключення до бази для типу <c>database</c> (формат Npgsql або MySqlConnector за <c>engine</c>).</summary>
    public const string ConnectionString = "$connectionString";

    /// <summary>Обчислювані значення і типи credentials, для яких вони мають сенс.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Computed { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [AccessToken] = [OAuth2Credential.Type, OAuth2Credential.ClientCredentialsType],
        [Authorization] = [CredentialTypes.Bearer, CredentialTypes.Basic, CredentialTypes.TelegramBot, OAuth2Credential.Type, OAuth2Credential.ClientCredentialsType],
        [ConnectionString] = [CredentialTypes.Database],
    };

    /// <summary>Чи потрібен серверу токен OAuth2 — тоді credential читається з отриманням токена.</summary>
    /// <param name="map">Відображення з <c>config.credentialMap</c>.</param>
    public static bool UsesAccessToken(IReadOnlyDictionary<string, string>? map) =>
        map is not null && (map.ContainsKey(AccessToken) || map.ContainsKey(Authorization));

    /// <summary>Чи може поле <paramref name="source"/> бути джерелом для типу <paramref name="type"/>.</summary>
    /// <param name="type">Тип credential'а з маніфесту.</param>
    /// <param name="source">Ключ відображення.</param>
    public static bool IsSource(CredentialTypeInfo type, string source)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Computed.TryGetValue(source, out var types)
            ? types.Contains(type.Type)
            : type.Fields.Any(f => f.Name == source && f.Kind is not (CredentialFieldKind.Managed or CredentialFieldKind.SecretMap));
    }

    /// <summary>Помилка цілі відображення або <c>null</c>.</summary>
    /// <param name="target">Назва змінної (stdio) або заголовок (http).</param>
    /// <param name="http">Ціль — заголовок.</param>
    public static string? TargetError(string? target, bool http)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "ціль порожня.";
        }

        if (!http)
        {
            return EnvName().IsMatch(target) ? null : $"«{target}» — не назва змінної середовища (латинські літери, цифри, «_»).";
        }

        return HeaderName().IsMatch(SplitHeader(target).Name) ? null : $"«{target}» — не заголовок HTTP (Назва або «Назва: префікс»).";
    }

    /// <summary>Перевіряє відображення в <c>config</c> підключення.</summary>
    /// <param name="map">Відображення.</param>
    /// <param name="http">Транспорт http (цілі — заголовки).</param>
    /// <exception cref="ArgumentException">Порожній ключ або некоректна ціль.</exception>
    public static void Validate(IReadOnlyDictionary<string, string>? map, bool http)
    {
        foreach (var (source, target) in map ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("config.credentialMap: порожнє поле credential'а.", nameof(map));
            }

            if (TargetError(target, http) is { } error)
            {
                throw new ArgumentException($"config.credentialMap.{source}: {error}", nameof(map));
            }
        }
    }

    /// <summary>Значення змінних середовища за відображенням.</summary>
    /// <param name="map">Відображення.</param>
    /// <param name="credential">Розшифровані дані; <c>null</c> — нічого.</param>
    /// <exception cref="InvalidOperationException">Значення поля — не рядок.</exception>
    public static IEnumerable<KeyValuePair<string, string>> Environment(IReadOnlyDictionary<string, string>? map, JsonObject? credential)
    {
        foreach (var (source, target) in map ?? new Dictionary<string, string>())
        {
            if (Resolve(source, credential) is { } value)
            {
                yield return new(target, value);
            }
        }
    }

    /// <summary>Заголовки за відображенням: <c>Назва: префікс</c> дає <c>префікс значення</c>.</summary>
    /// <param name="map">Відображення.</param>
    /// <param name="credential">Розшифровані дані; <c>null</c> — нічого.</param>
    /// <exception cref="InvalidOperationException">Значення поля — не рядок.</exception>
    public static IEnumerable<KeyValuePair<string, string>> Headers(IReadOnlyDictionary<string, string>? map, JsonObject? credential)
    {
        foreach (var (source, target) in map ?? new Dictionary<string, string>())
        {
            if (Resolve(source, credential) is { } value)
            {
                var (name, prefix) = SplitHeader(target);
                yield return new(name, prefix is null ? value : $"{prefix} {value}");
            }
        }
    }

    /// <summary>Значення поля або обчислюване значення; <c>null</c> — немає в даних.</summary>
    /// <param name="source">Ключ відображення.</param>
    /// <param name="credential">Розшифровані дані.</param>
    /// <exception cref="InvalidOperationException">Значення поля — не рядок.</exception>
    public static string? Resolve(string source, JsonObject? credential)
    {
        if (credential is null)
        {
            return null;
        }

        switch (source)
        {
            case AccessToken:
                return NonEmpty(Text(credential, "accessToken"));
            case Authorization:
                return OAuth2Credential.AuthorizationHeader(credential)
                       ?? (NonEmpty(Text(credential, "token")) is { } token ? $"Bearer {token}" : null)
                       ?? (NonEmpty(Text(credential, "username")) is { } user
                           ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{Text(credential, "password")}"))
                           : null);
            case ConnectionString:
                return BuildConnectionString(credential);
            default:
                return NonEmpty(Text(credential, source));
        }
    }

    /// <summary>
    /// Рядок підключення типу <c>database</c>: <c>Host/Port/Database/Username/Password/SSL Mode</c> для postgres,
    /// <c>Server/Port/Database/User ID/Password/SslMode</c> для mysql. Значення екрануються <see cref="DbConnectionStringBuilder"/>.
    /// </summary>
    /// <param name="credential">Розшифровані дані.</param>
    /// <returns><c>null</c>, якщо немає <c>engine</c> або <c>host</c>.</returns>
    public static string? BuildConnectionString(JsonObject credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var engine = NonEmpty(Text(credential, "engine"));
        var host = NonEmpty(Text(credential, "host"));
        if (engine is null || host is null)
        {
            return null;
        }

        var mysql = engine == "mysql";
        var builder = new DbConnectionStringBuilder
        {
            [mysql ? "Server" : "Host"] = host,
        };
        Add("Port", Text(credential, "port"));
        Add("Database", Text(credential, "database"));
        Add(mysql ? "User ID" : "Username", Text(credential, "username"));
        Add("Password", Text(credential, "password"));
        if (NonEmpty(Text(credential, "ssl")) is { } ssl)
        {
            builder[mysql ? "SslMode" : "SSL Mode"] = (mysql, ssl) switch
            {
                (true, "disable") => "None",
                (true, "require") => "Required",
                (true, _) => "Preferred",
                (false, "disable") => "Disable",
                (false, "require") => "Require",
                _ => "Prefer",
            };
        }

        return builder.ConnectionString;

        void Add(string key, string? value)
        {
            if (NonEmpty(value) is { } text)
            {
                builder[key] = text;
            }
        }
    }

    private static (string Name, string? Prefix) SplitHeader(string target)
    {
        var colon = target.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return (target.Trim(), null);
        }

        var prefix = target[(colon + 1)..].Trim();
        return (target[..colon].Trim(), prefix.Length == 0 ? null : prefix);
    }

    private static string? Text(JsonObject credential, string name) => credential[name] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonValue v when v.TryGetValue<double>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"значення «{name}» у credential має бути рядком."),
    };

    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvName();

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderName();
}
