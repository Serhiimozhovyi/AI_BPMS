namespace OpenBP.Connectors.MsGraph;

/// <summary>
/// Реєстрація застосунку в Entra ID — **лише** зі змінних середовища (OpenBP передає їх через credential
/// <c>{ "env": { … } }</c> конектора). Секрет застосунку й отримані токени ніколи не потрапляють у відповіді tools і
/// тексти помилок: <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>Id тенанта (Directory ID).</summary>
    public const string TenantIdVariable = "TENANT_ID";

    /// <summary>Id застосунку (Application/client ID).</summary>
    public const string ClientIdVariable = "CLIENT_ID";

    /// <summary>Секрет застосунку.</summary>
    public const string ClientSecretVariable = "CLIENT_SECRET";

    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);

    /// <summary>Значення змінної.</summary>
    /// <param name="variable">Назва.</param>
    /// <exception cref="ConnectorException">Змінну не задано.</exception>
    public string Require(string variable) =>
        environment(variable) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Не задано змінну середовища {variable} (credential конектора в OpenBP, поле env).");

    /// <summary>Запам'ятовує виданий токен, щоб <see cref="Scrub"/> прибирав і його.</summary>
    /// <param name="token">Access token.</param>
    public void Remember(string token)
    {
        lock (_issued)
        {
            _issued.Add(token);
        }
    }

    /// <summary>Замінює секрет застосунку й видані токени в тексті на <c>***</c>.</summary>
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
