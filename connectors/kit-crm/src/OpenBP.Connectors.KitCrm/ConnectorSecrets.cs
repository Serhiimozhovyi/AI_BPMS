namespace OpenBP.Connectors.KitCrm;

/// <summary>
/// Доступ до Кіта — **лише** зі змінних середовища (AI_BPMS передає їх через credential <c>{ "env": { … } }</c>
/// конектора): client Keycloak із service account (<c>client_credentials</c>, docs/kit-sales.md §5.1) і необов'язковий
/// заголовок <c>FrontPagePath</c>. Секрет client'а й видані токени ніколи не потрапляють у відповіді tools і тексти
/// помилок: <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>Адреса видачі токена Keycloak, напр. <c>https://sso/realms/astarta/protocol/openid-connect/token</c>.</summary>
    public const string TokenUrlVariable = "KIT_TOKEN_URL";

    /// <summary>Id client'а Keycloak.</summary>
    public const string ClientIdVariable = "KIT_CLIENT_ID";

    /// <summary>Секрет client'а.</summary>
    public const string ClientSecretVariable = "KIT_CLIENT_SECRET";

    /// <summary>Значення заголовка <c>FrontPagePath</c> для запису; порожньо — заголовок не шлеться.</summary>
    public const string FrontPagePathVariable = "KIT_FRONT_PAGE_PATH";

    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

    /// <summary>Значення змінної.</summary>
    /// <param name="variable">Назва.</param>
    /// <exception cref="ConnectorException">Змінну не задано.</exception>
    public string Require(string variable) =>
        environment(variable) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Не задано змінну середовища {variable} (credential конектора в AI_BPMS, поле env).");

    /// <summary><c>FrontPagePath</c> або <c>null</c>.</summary>
    public string? FrontPagePath => environment(FrontPagePathVariable) is { Length: > 0 } value ? value.Trim() : null;

    /// <summary>Запам'ятовує виданий токен, щоб <see cref="Scrub"/> прибирав і його.</summary>
    /// <param name="token">Access token.</param>
    public void Remember(string token)
    {
        lock (_issued)
        {
            _issued.Add(token);
        }
    }

    /// <summary>Замінює секрет client'а й видані токени в тексті на <c>***</c>.</summary>
    /// <param name="text">Текст, напр. тіло відповіді з помилкою.</param>
    public string Scrub(string text)
    {
        if (environment(ClientSecretVariable) is { Length: > 0 } secret)
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        lock (_issued)
        {
            foreach (var token in _issued)
            {
                text = text.Replace(token, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без секретів): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
