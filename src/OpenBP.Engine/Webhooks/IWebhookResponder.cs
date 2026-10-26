namespace OpenBP.Engine.Webhooks;

/// <summary>HTTP-відповідь викликачу webhook-а (F6-05): статус, заголовки (зокрема <c>Content-Type</c>) і тіло.</summary>
/// <param name="StatusCode">HTTP-статус, 100…599.</param>
/// <param name="Headers">Заголовки відповіді: ім'я → значення.</param>
/// <param name="Body">Тіло (можна порожнє).</param>
public sealed record WebhookResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body);

/// <summary>
/// Передає відповідь webhook-а з виконання в Api (F6-05, <c>core.respondToWebhook</c> і режим <c>whenFinished</c>):
/// воркер пише подію відповіді, Api, що тримає HTTP-запит, віддає її викликачу. Перша відповідь виконання виграє,
/// наступні ігноруються. Хост дає реалізацію; без неї вузол дає помилку.
/// </summary>
public interface IWebhookResponder
{
    /// <summary>Записує відповідь виконання <paramref name="executionId"/>, якщо її ще немає.</summary>
    /// <param name="executionId">Виконання, запущене webhook-ом.</param>
    /// <param name="response">Відповідь.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns><c>true</c> — записано; <c>false</c> — відповідь уже була.</returns>
    Task<bool> RespondAsync(Guid executionId, WebhookResponse response, CancellationToken ct);
}
