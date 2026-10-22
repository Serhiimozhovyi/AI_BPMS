namespace OpenBP.Data.Credentials;

/// <summary>
/// Ключ AES-256-GCM для таблиці <c>credentials</c> (docs/architecture.md §8, правило 6 <c>CLAUDE.md</c>):
/// 32 байти, у змінній середовища <see cref="EnvironmentVariable"/> як base64. Без коректного ключа хост
/// не стартує — <see cref="FromEnvironment"/> кидає виняток. Значення ключа не потрапляє в повідомлення помилок.
/// </summary>
public sealed class CredentialEncryptionKey
{
    /// <summary>Назва змінної середовища з ключем.</summary>
    public const string EnvironmentVariable = "OPENBP_ENCRYPTION_KEY";

    /// <summary>Довжина ключа в байтах (AES-256).</summary>
    public const int KeySize = 32;

    private readonly byte[] _key;

    private CredentialEncryptionKey(byte[] key) => _key = key;

    /// <summary>Байти ключа (копія не робиться — лише для <see cref="CredentialStore"/>).</summary>
    internal ReadOnlySpan<byte> Bytes => _key;

    /// <summary>Розбирає ключ із base64.</summary>
    /// <param name="base64">32 байти, закодовані base64.</param>
    /// <exception cref="InvalidOperationException">Ключ порожній, не base64 або не 32 байти.</exception>
    public static CredentialEncryptionKey FromBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException(
                $"Не задано ключ шифрування credentials: змінна {EnvironmentVariable} має містити {KeySize} байти у base64.");
        }

        var buffer = new byte[base64.Length];
        if (!Convert.TryFromBase64String(base64.Trim(), buffer, out var written))
        {
            throw new InvalidOperationException($"Ключ {EnvironmentVariable} не є коректним base64.");
        }

        if (written != KeySize)
        {
            throw new InvalidOperationException(
                $"Ключ {EnvironmentVariable} має бути {KeySize} байти, а отримано {written}.");
        }

        return new CredentialEncryptionKey(buffer[..KeySize]);
    }

    /// <summary>Читає ключ зі змінної <see cref="EnvironmentVariable"/>; викликається при старті Api/Worker.</summary>
    /// <exception cref="InvalidOperationException">Змінної немає або ключ некоректний.</exception>
    public static CredentialEncryptionKey FromEnvironment() =>
        FromBase64(Environment.GetEnvironmentVariable(EnvironmentVariable));
}
