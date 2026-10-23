namespace OpenBP.Connectors.OneC;

/// <summary>
/// Логін і пароль користувача 1С — **лише** зі змінних середовища (OpenBP передає їх через credential
/// <c>{ "env": { … } }</c> конектора). Пароль ніколи не потрапляє у відповіді tools і тексти помилок: <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>Ім'я користувача 1С з правом на OData.</summary>
    public const string UsernameVariable = "ONEC_USERNAME";

    /// <summary>Пароль; у 1С буває порожнім, тому змінна необов'язкова.</summary>
    public const string PasswordVariable = "ONEC_PASSWORD";

    /// <summary>Ім'я користувача.</summary>
    /// <exception cref="ConnectorException">Змінну не задано.</exception>
    public string Username =>
        environment(UsernameVariable) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Не задано змінну середовища {UsernameVariable} (credential конектора в OpenBP, поле env).");

    /// <summary>Пароль (порожній, якщо змінну не задано).</summary>
    public string Password => environment(PasswordVariable) ?? "";

    /// <summary>Замінює пароль у тексті на <c>***</c>.</summary>
    /// <param name="text">Текст, напр. тіло відповіді 1С.</param>
    public string Scrub(string text) =>
        environment(PasswordVariable) is { Length: > 0 } secret ? text.Replace(secret, "***", StringComparison.Ordinal) : text;
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без секретів): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
