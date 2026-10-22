namespace OpenBP.Data.Credentials;

/// <summary>
/// Дані credential'а не розшифровуються: інший ключ <see cref="CredentialEncryptionKey.EnvironmentVariable"/>,
/// пошкоджені <c>data_encrypted</c>/<c>nonce</c> або рядок підмінено. Повідомлення не містить ні ключа, ні даних.
/// </summary>
public sealed class CredentialDecryptionException : Exception
{
    /// <summary>Створює виняток для id credential'а.</summary>
    /// <param name="credentialId">Id рядка <c>credentials</c>.</param>
    /// <param name="innerException">Причина (зазвичай <see cref="System.Security.Cryptography.CryptographicException"/>).</param>
    public CredentialDecryptionException(Guid credentialId, Exception? innerException)
        : base(
            $"Не вдалося розшифрувати credential «{credentialId}»: ключ {CredentialEncryptionKey.EnvironmentVariable} інший або дані пошкоджені.",
            innerException)
    {
        CredentialId = credentialId;
    }

    /// <summary>Id credential'а.</summary>
    public Guid CredentialId { get; }
}
