namespace Connector.Template;

/// <summary>
/// Секрети конектора — **лише** зі змінних середовища (AI_BPMS передає їх через credential <c>{ "env": { … } }</c>
/// конектора). Значення ніколи не потрапляють у відповіді tools і тексти помилок: <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>Логін для <c>basic</c>.</summary>
    public const string UsernameVariable = "CONNECTOR_USERNAME";

    /// <summary>Пароль для <c>basic</c>.</summary>
    public const string PasswordVariable = "CONNECTOR_PASSWORD";

    /// <summary>Токен для <c>bearer</c>.</summary>
    public const string TokenVariable = "CONNECTOR_TOKEN";

    /// <summary>Ключ для <c>api-key</c>.</summary>
    public const string ApiKeyVariable = "CONNECTOR_API_KEY";

    private static readonly string[] Variables = [UsernameVariable, PasswordVariable, TokenVariable, ApiKeyVariable];

    /// <summary>Значення змінної.</summary>
    /// <param name="variable">Назва.</param>
    /// <exception cref="ConnectorException">Змінну не задано.</exception>
    public string Require(string variable) =>
        environment(variable) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Не задано змінну середовища {variable} (credential конектора в AI_BPMS, поле env).");

    /// <summary>Замінює значення всіх секретів у тексті на <c>***</c>.</summary>
    /// <param name="text">Текст, напр. тіло відповіді REST.</param>
    public string Scrub(string text)
    {
        foreach (var variable in Variables)
        {
            if (environment(variable) is { Length: > 0 } secret)
            {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }

        return text;
    }
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без секретів): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
