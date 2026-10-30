using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.GSheets;

/// <summary>
/// Ключ сервісного акаунта Google — **лише** зі змінної середовища <see cref="KeyVariable"/> (AI_BPMS передає її через
/// credential <c>{ "env": { … } }</c> конектора). Закритий ключ, сам JSON, підписані JWT і видані токени ніколи не
/// потрапляють у відповіді tools, тексти помилок і журнали: <see cref="Scrub"/>.
/// </summary>
/// <param name="environment">Джерело змінних (за замовчуванням — середовище процесу; у тестах — словник).</param>
public sealed class ConnectorSecrets(Func<string, string?> environment)
{
    /// <summary>JSON-ключ сервісного акаунта (файл, завантажений з Google Cloud Console), повністю.</summary>
    public const string KeyVariable = "GOOGLE_SERVICE_ACCOUNT_JSON";

    private readonly HashSet<string> _issued = new(StringComparer.Ordinal);
    private ServiceAccountKey? _key;

    /// <summary>Розібраний ключ сервісного акаунта.</summary>
    /// <exception cref="ConnectorException">Змінну не задано або це не ключ сервісного акаунта.</exception>
    public ServiceAccountKey Key()
    {
        if (_key is not null)
        {
            return _key;
        }

        if (environment(KeyVariable) is not { Length: > 0 } json)
        {
            throw new ConnectorException($"Не задано змінну середовища {KeyVariable} (credential конектора в AI_BPMS, поле env): JSON-ключ сервісного акаунта.");
        }

        ServiceAccountKey? key;
        try
        {
            key = JsonSerializer.Deserialize<ServiceAccountKey>(json);
        }
        catch (JsonException)
        {
            // Текст винятку може містити фрагмент ключа — не показуємо.
            throw new ConnectorException($"{KeyVariable} — не JSON. Вставте вміст файлу ключа сервісного акаунта повністю.");
        }

        if (key is not { ClientEmail.Length: > 0, PrivateKey.Length: > 0 })
        {
            throw new ConnectorException($"{KeyVariable}: у ключі немає client_email або private_key — потрібен ключ сервісного акаунта (type: service_account).");
        }

        return _key = key;
    }

    /// <summary>Запам'ятовує підписаний JWT чи виданий токен, щоб <see cref="Scrub"/> прибирав і його.</summary>
    /// <param name="value">Значення.</param>
    public void Remember(string value)
    {
        lock (_issued)
        {
            _issued.Add(value);
        }
    }

    /// <summary>Замінює JSON ключа, закритий ключ (і кожен його рядок), JWT і токени в тексті на <c>***</c>.</summary>
    /// <param name="text">Текст, напр. тіло відповіді з помилкою.</param>
    public string Scrub(string text)
    {
        if (environment(KeyVariable) is { Length: > 0 } json)
        {
            text = text.Replace(json, "***", StringComparison.Ordinal);
            string? pem = null;
            try
            {
                pem = JsonSerializer.Deserialize<ServiceAccountKey>(json)?.PrivateKey;
            }
            catch (JsonException)
            {
                // Не JSON — прибрали сам рядок вище.
            }

            if (pem is { Length: > 0 })
            {
                text = text.Replace(pem, "***", StringComparison.Ordinal);
                foreach (var line in pem.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!line.StartsWith("-----", StringComparison.Ordinal) && line.Length >= 16)
                    {
                        text = text.Replace(line, "***", StringComparison.Ordinal);
                    }
                }
            }
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

/// <summary>Потрібні поля ключа сервісного акаунта Google.</summary>
/// <param name="ClientEmail">Email сервісного акаунта — йому треба надати доступ до таблиці.</param>
/// <param name="PrivateKey">Закритий ключ RSA у PEM (PKCS#8).</param>
/// <param name="PrivateKeyId">Id ключа (<c>kid</c> у JWT).</param>
/// <param name="TokenUri">Адреса обміну JWT на токен.</param>
public sealed record ServiceAccountKey(
    [property: JsonPropertyName("client_email")] string? ClientEmail,
    [property: JsonPropertyName("private_key")] string? PrivateKey,
    [property: JsonPropertyName("private_key_id")] string? PrivateKeyId,
    [property: JsonPropertyName("token_uri")] string? TokenUri);

/// <summary>Помилка конектора, текст якої безпечно показати агенту (без секретів): tool повертає її як <c>isError</c>.</summary>
/// <param name="message">Повідомлення.</param>
public sealed class ConnectorException(string message) : Exception(message);
