namespace OpenBP.Engine;

/// <summary>Базовий виняток движка; усі помилки, які движок кидає сам, походять від нього.</summary>
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
