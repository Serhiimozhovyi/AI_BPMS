using System.Text.Json.Nodes;
using OpenBP.Engine;

namespace OpenBP.Data.Credentials;

/// <summary>
/// <see cref="ICredentialProvider"/> над <see cref="CredentialStore"/> (docs/architecture.md §8, §10.4): воркер
/// розшифровує credential у момент виконання вузла. Кожне отримане значення реєструється в
/// <see cref="SecretScrubber"/> виконання, тож <see cref="PostgresExecutionStore"/> з тим самим scrubber'ом
/// пише кроки вже без нього. Створюється на одне виконання.
/// </summary>
/// <param name="store">Сховище credentials.</param>
/// <param name="scrubber">Scrubber виконання; <c>null</c> — значення не реєструються.</param>
public sealed class PostgresCredentialProvider(CredentialStore store, SecretScrubber? scrubber = null) : ICredentialProvider
{
    private readonly CredentialStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <inheritdoc />
    /// <exception cref="CredentialDecryptionException">Дані не розшифровуються.</exception>
    public async Task<JsonObject> GetAsync(string credentialId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        if (!Guid.TryParse(credentialId, out var id))
        {
            throw new CredentialNotFoundException(credentialId);
        }

        var data = await _store.GetDataAsync(id, ct).ConfigureAwait(false)
            ?? throw new CredentialNotFoundException(credentialId);
        scrubber?.Add(data);
        return data;
    }
}
