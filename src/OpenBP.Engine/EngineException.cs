namespace OpenBP.Engine;

/// <summary>
/// Базовий виняток движка; усі помилки, які движок кидає сам, походять від нього.
/// <see cref="Exception.Message"/> — для людини за правилом «що сталося → що зробити» (F3-57): без стеку,
/// без слів «помилка»/«exception»; технічні подробиці (відповідь сервера, текст Jint) — у <see cref="Details"/>.
/// </summary>
public class EngineException : Exception
{
    /// <summary>Створює виняток із повідомленням.</summary>
    public EngineException(string message)
        : base(message)
    {
    }

    /// <summary>Створює виняток із повідомленням і причиною.</summary>
    public EngineException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Технічні подробиці для розбору (не для картки вузла); <c>null</c> — немає.</summary>
    public string? Details { get; init; }

    /// <summary>Індекс item'а на вході вузла, на якому сталося (від 0); <c>null</c> — невідомо або не стосується item'а.</summary>
    public int? ItemIndex { get; init; }

    /// <summary>
    /// Повідомлення з позначкою item'а в кінці першого речення: «Поле «x» порожнє [item 2]. Вкажіть значення.».
    /// Позначка — лише коли на вході більше одного item'а (<paramref name="itemCount"/>).
    /// </summary>
    /// <param name="message">Повідомлення «що сталося. Що зробити.».</param>
    /// <param name="itemIndex">Індекс item'а від 0.</param>
    /// <param name="itemCount">Скільки items на вході вузла.</param>
    public static string AtItem(string message, int itemIndex, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (itemCount <= 1 || message.Contains("[item ", StringComparison.Ordinal))
        {
            return message;
        }

        var label = $" [item {itemIndex}]";
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        if (end < 0)
        {
            end = message.EndsWith('.') ? message.Length - 1 : message.Length;
        }

        return message.Insert(end, label);
    }
}

/// <summary>Тип вузла невідомий реєстру (<see cref="INodeRegistry.Resolve"/>).</summary>
public sealed class NodeTypeNotFoundException : EngineException
{
    /// <summary>Створює виняток для типу і версії вузла.</summary>
    public NodeTypeNotFoundException(string type, int typeVersion)
        : base($"Вузол «{type}» версії {typeVersion} не зареєстровано.")
    {
        Type = type;
        TypeVersion = typeVersion;
    }

    /// <summary>Запитаний тип вузла.</summary>
    public string Type { get; }

    /// <summary>Запитана версія типу.</summary>
    public int TypeVersion { get; }
}

/// <summary>Credential із таким id не існує або недоступний (<see cref="ICredentialProvider.GetAsync"/>).</summary>
public sealed class CredentialNotFoundException : EngineException
{
    /// <summary>Створює виняток для id credential'а.</summary>
    public CredentialNotFoundException(string credentialId)
        : base($"Credential «{credentialId}» не знайдено.")
    {
        CredentialId = credentialId;
    }

    /// <summary>Запитаний id credential'а.</summary>
    public string CredentialId { get; }
}

/// <summary>Файлу з таким id немає у бінарному сховищі (<see cref="IBinaryStore.OpenReadAsync"/>).</summary>
public sealed class BinaryNotFoundException : EngineException
{
    /// <summary>Створює виняток для ключа файлу.</summary>
    public BinaryNotFoundException(string id)
        : base($"Файл «{id}» не знайдено у бінарному сховищі.")
    {
        Id = id;
    }

    /// <summary>Запитаний ключ файлу.</summary>
    public string Id { get; }
}
