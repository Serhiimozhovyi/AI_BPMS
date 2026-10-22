using System.Text.Json.Nodes;

namespace OpenBP.Engine.Testing;

/// <summary>
/// <see cref="ICredentialProvider"/> для тестів і CLI: credentials задаються у коді або з JSON-файлу,
/// без шифрування. Повертає копію даних на кожен запит. Потокобезпечний.
/// </summary>
public sealed class FakeCredentialProvider : ICredentialProvider
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, JsonObject> _credentials = new(StringComparer.Ordinal);

    /// <summary>Додає або замінює credential. Дані копіюються.</summary>
    /// <param name="credentialId">Id, на який посилається сценарій.</param>
    /// <param name="data">Розшифровані дані, напр. <c>{ "apiKey": "test" }</c>.</param>
    /// <returns>Цей провайдер — для ланцюжка викликів.</returns>
    public FakeCredentialProvider Add(string credentialId, JsonObject data)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock)
        {
            _credentials[credentialId] = (JsonObject)data.DeepClone();
        }

        return this;
    }

    /// <summary>Видаляє credential; <c>false</c>, якщо такого не було.</summary>
    /// <param name="credentialId">Id credential'а.</param>
    public bool Remove(string credentialId)
    {
        lock (_lock)
        {
            return _credentials.Remove(credentialId);
        }
    }

    /// <inheritdoc />
    public Task<JsonObject> GetAsync(string credentialId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialId);
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_credentials.TryGetValue(credentialId, out var data))
            {
                throw new CredentialNotFoundException(credentialId);
            }

            return Task.FromResult((JsonObject)data.DeepClone());
        }
    }
}
