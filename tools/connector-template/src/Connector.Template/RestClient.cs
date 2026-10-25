using System.Net.Http.Json;
using System.Text.Json;

namespace Connector.Template;

/// <summary>
/// Типізований <see cref="HttpClient"/> до REST API (базова адреса й автентифікація — з <see cref="ConnectorHost"/>).
/// Неуспішна відповідь → <see cref="ConnectorException"/> зі статусом і початком тіла, очищеним від секретів.
/// Тут — приклад над фейковим API товарів (<c>/items</c>); замініть на методи свого API.
/// </summary>
/// <param name="http">Клієнт.</param>
/// <param name="secrets">Секрети — для очищення текстів помилок.</param>
public sealed class RestClient(HttpClient http, ConnectorSecrets secrets)
{
    /// <summary>Скільки символів тіла помилки показувати.</summary>
    public const int ErrorBodyLimit = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>GET /items?search=</c>.</summary>
    /// <param name="search">Пошук за назвою.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<Item>> ListItemsAsync(string? search, CancellationToken ct) =>
        GetAsync<IReadOnlyList<Item>>(string.IsNullOrWhiteSpace(search) ? "items" : $"items?search={Uri.EscapeDataString(search)}", ct);

    /// <summary><c>GET /items/{id}</c>.</summary>
    /// <param name="id">Id товару.</param>
    /// <param name="ct">Скасування.</param>
    public Task<Item> GetItemAsync(string id, CancellationToken ct) =>
        GetAsync<Item>($"items/{Uri.EscapeDataString(id)}", ct);

    /// <summary><c>GET /items?updatedAfter=</c> — записи, змінені строго після <paramref name="since"/>, від старих до нових.</summary>
    /// <param name="since">Курсор попередньої звірки; <c>null</c> — усі записи.</param>
    /// <param name="ct">Скасування.</param>
    public Task<IReadOnlyList<Item>> ListItemsChangedAsync(DateTimeOffset? since, CancellationToken ct) =>
        GetAsync<IReadOnlyList<Item>>(
            since is null ? "items?orderBy=updated" : $"items?orderBy=updated&updatedAfter={Uri.EscapeDataString(since.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}",
            ct);

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
               ?? throw new ConnectorException($"REST {path}: порожня відповідь.");
    }

    /// <summary>
    /// Надсилає запит (шлях — відносно базової адреси) і повертає успішну відповідь; інакше — <see cref="ConnectorException"/>.
    /// Ним користуються й згенеровані tools (<see cref="OpenApi.OpenApiTool"/>).
    /// </summary>
    /// <param name="request">Запит.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(secrets.Scrub($"REST API недоступний: {ex.Message}"));
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > ErrorBodyLimit)
            {
                body = body[..ErrorBodyLimit] + "…";
            }

            throw new ConnectorException(secrets.Scrub(
                $"REST {request.Method} {request.RequestUri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                (body.Length > 0 ? $" — {body}" : "")));
        }
    }
}

/// <summary>Товар фейкового API (приклад).</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Назва.</param>
/// <param name="Price">Ціна.</param>
/// <param name="Updated">Час останньої зміни (UTC) — за ним <c>changes_since</c> будує курсор.</param>
public sealed record Item(string Id, string Name, decimal Price, DateTimeOffset Updated);
