namespace OpenBP.Connectors.Telegram;

/// <summary>
/// Токен бота — **лише** зі змінної середовища <see cref="BotTokenVariable"/> (AI_BPMS передає її через credential
/// <c>{ "env": { "TELEGRAM_BOT_TOKEN": "…" } }</c> конектора). Токен стоїть у шляху запитів Bot API, тому кожен текст
/// помилки проходить <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>Токен бота від @BotFather.</summary>
    public const string BotTokenVariable = "TELEGRAM_BOT_TOKEN";

    /// <summary>Токен бота.</summary>
    /// <exception cref="ConnectorException">Змінну не задано.</exception>
    public string BotToken =>
        environment(BotTokenVariable) is { Length: > 0 } value
            ? value
            : throw new ConnectorException($"Не задано змінну середовища {BotTokenVariable} (credential конектора в AI_BPMS, поле env).");

    /// <summary>Замінює токен у тексті на <c>***</c>.</summary>
    /// <param name="text">Текст, напр. повідомлення про помилку з URL запиту.</param>
    public string Scrub(string text) =>
        environment(BotTokenVariable) is { Length: > 0 } token ? text.Replace(token, "***", StringComparison.Ordinal) : text;
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без токена): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
/// <param name="statusCode">HTTP-статус відповіді Bot API, якщо вона була.</param>
public sealed class ConnectorException(string message, int? statusCode = null) : Exception(message)
{
    /// <summary>HTTP-статус відповіді Bot API; <c>null</c> — помилка до запиту або мережі.</summary>
    public int? StatusCode { get; } = statusCode;
}
