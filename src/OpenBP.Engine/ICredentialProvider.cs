using System.Text.Json.Nodes;

namespace OpenBP.Engine;

/// <summary>
/// Джерело розшифрованих credentials (docs/architecture.md §2, §8). Розшифровка відбувається лише у воркері
/// в момент виконання; значення ніколи не потрапляють у логи, кроки чи відповіді API.
/// Реалізації: на базі таблиці <c>credentials</c> (F2-A) і <c>FakeCredentialProvider</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface ICredentialProvider
{
    /// <summary>
    /// Повертає розшифровані дані credential'а (напр. <c>{ "apiKey": "…" }</c>).
    /// Кожен виклик повертає окрему копію — вузол може її змінювати, не впливаючи на інших.
    /// </summary>
    /// <param name="credentialId">Id credential'а зі сценарію (<c>credentialId</c> у параметрах вузла).</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="CredentialNotFoundException">Credential не існує або недоступний.</exception>
    Task<JsonObject> GetAsync(string credentialId, CancellationToken ct);
}
