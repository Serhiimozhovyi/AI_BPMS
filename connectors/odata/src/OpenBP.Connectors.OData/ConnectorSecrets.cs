using System.Text;

namespace OpenBP.Connectors.OData;

/// <summary>Спосіб входу, який визначається за заданими змінними середовища.</summary>
public enum AuthKind
{
    /// <summary>Сервіс відкритий.</summary>
    None,

    /// <summary><c>oauth2.clientCredentials</c>: токен від <c>ODATA_TOKEN_URL</c>.</summary>
    OAuth2,

    /// <summary>Готовий токен <c>ODATA_TOKEN</c> в <c>Authorization: Bearer</c>.</summary>
    Bearer,

    /// <summary>Ім'я й пароль в <c>Authorization: Basic</c>.</summary>
    Basic,
}

/// <summary>
/// Доступ до сервісу OData — **лише** зі змінних середовища (OpenBP передає їх з загального credential'а через
/// <c>credential.map</c> маніфесту): client credentials (<c>ODATA_TOKEN_URL</c>, <c>ODATA_CLIENT_ID</c>,
/// <c>ODATA_CLIENT_SECRET</c>, необов'язково <c>ODATA_SCOPES</c>, <c>ODATA_AUDIENCE</c>), токен (<c>ODATA_TOKEN</c>) або
/// <c>ODATA_USERNAME</c>/<c>ODATA_PASSWORD</c>. Секрети, видані токени й заголовок Basic ніколи не потрапляють у відповіді
/// tools і тексти помилок: <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>Адреса видачі токена.</summary>
    public const string TokenUrlVariable = "ODATA_TOKEN_URL";

    /// <summary>Id client'а.</summary>
    public const string ClientIdVariable = "ODATA_CLIENT_ID";

    /// <summary>Секрет client'а.</summary>
    public const string ClientSecretVariable = "ODATA_CLIENT_SECRET";

    /// <summary>Scope (через пробіл).</summary>
    public const string ScopesVariable = "ODATA_SCOPES";

    /// <summary>Audience.</summary>
    public const string AudienceVariable = "ODATA_AUDIENCE";

    /// <summary>Готовий токен.</summary>
    public const string TokenVariable = "ODATA_TOKEN";

    /// <summary>Ім'я для Basic.</summary>
    public const string UsernameVariable = "ODATA_USERNAME";

    /// <summary>Пароль для Basic.</summary>
    public const string PasswordVariable = "ODATA_PASSWORD";

    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

    /// <summary>Спосіб входу: токен-сервіс, далі готовий токен, далі ім'я; нічого — відкритий сервіс.</summary>
    public AuthKind Kind =>
        Get(TokenUrlVariable) is not null ? AuthKind.OAuth2
        : Get(TokenVariable) is not null ? AuthKind.Bearer
        : Get(UsernameVariable) is not null ? AuthKind.Basic
        : AuthKind.None;

    /// <summary>Значення змінної або <c>null</c>.</summary>
    /// <param name="variable">Назва.</param>
    public string? Get(string variable) => environment(variable) is { Length: > 0 } value ? value : null;

    /// <summary>Значення змінної.</summary>
    /// <param name="variable">Назва.</param>
    /// <exception cref="ConnectorException">Змінну не задано.</exception>
    public string Require(string variable) =>
        Get(variable) ?? throw new ConnectorException($"Не задано змінну середовища {variable} (облікові дані підключення в OpenBP).");

    /// <summary>Запам'ятовує виданий токен чи заголовок, щоб <see cref="Scrub"/> прибирав і їх.</summary>
    /// <param name="value">Access token, значення заголовка.</param>
    public void Remember(string value)
    {
        lock (_issued)
        {
            _issued.Add(value);
        }
    }

    /// <summary>Замінює секрети, видані токени й Basic-заголовок у тексті на <c>***</c>.</summary>
    /// <param name="text">Текст, напр. тіло відповіді з помилкою.</param>
    public string Scrub(string text)
    {
        foreach (var variable in new[] { ClientSecretVariable, TokenVariable, PasswordVariable })
        {
            if (Get(variable) is { } secret)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        if (Get(UsernameVariable) is { } user && Get(PasswordVariable) is { } password)
        {
            text = text.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")), "***", StringComparison.Ordinal);
        }

        lock (_issued)
        {
            foreach (var value in _issued)
            {
                text = text.Replace(value, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без секретів): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
