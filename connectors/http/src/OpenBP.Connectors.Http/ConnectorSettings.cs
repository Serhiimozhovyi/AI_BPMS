namespace OpenBP.Connectors.Http;

/// <summary>
/// Обмеження на адреси: змінні середовища <see cref="AllowedHostsVariable"/> і <see cref="AllowPrivateVariable"/>
/// (AI_BPMS передає їх через credential конектора, поле <c>env</c>). Секретів тут немає.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSettings(Func<string, string?> environment)
{
    /// <summary>Дозволені хости через кому: <c>example.com</c> або <c>*.example.com</c>. Порожньо — будь-які публічні.</summary>
    public const string AllowedHostsVariable = "HTTP_ALLOWED_HOSTS";

    /// <summary>
    /// Хости через кому, яким дозволено приватні адреси (напр. <c>host.docker.internal</c> для локального Ollama чи FLUX);
    /// <c>*</c> — усім. Типово приватні адреси заборонені (захист від SSRF).
    /// </summary>
    public const string AllowPrivateVariable = "HTTP_ALLOW_PRIVATE";

    /// <summary>Дозволені хости; порожній список — будь-які.</summary>
    public IReadOnlyList<string> AllowedHosts => Split(AllowedHostsVariable);

    /// <summary>Хости з дозволеними приватними адресами.</summary>
    public IReadOnlyList<string> AllowPrivate => Split(AllowPrivateVariable);

    private List<string> Split(string variable) =>
    [
        .. (environment(variable) ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.ToLowerInvariant()),
    ];
}

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без секретів з URL): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
