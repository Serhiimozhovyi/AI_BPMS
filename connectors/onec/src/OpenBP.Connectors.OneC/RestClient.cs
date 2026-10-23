using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenBP.Connectors.OneC;

/// <summary>
/// Типізований <see cref="HttpClient"/> до OData-публікації 1С (базова адреса й автентифікація — з <see cref="ConnectorHost"/>).
/// Неуспішна відповідь → <see cref="ConnectorException"/> зі статусом і початком тіла, очищеним від пароля.
/// </summary>
/// <param name="http">Клієнт.</param>
/// <param name="secrets">Секрети — для очищення текстів помилок.</param>
public sealed class RestClient(HttpClient http, ConnectorSecrets secrets)
{
    /// <summary>Скільки символів тіла помилки показувати.</summary>
    public const int ErrorBodyLimit = 300;

    /// <summary><c>GET</c> з JSON-відповіддю.</summary>
    /// <param name="path">Шлях із query відносно базової адреси.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
        return await ReadAsync(response, path, ct).ConfigureAwait(false);
    }

    /// <summary><c>POST</c> JSON-тіла з JSON-відповіддю.</summary>
    /// <param name="path">Шлях із query відносно базової адреси.</param>
    /// <param name="body">Тіло.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<JsonElement> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, path, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            throw new ConnectorException($"OData {path}: відповідь не JSON (чи увімкнено Connector:OData:FormatParameter?).");
        }
    }

    /// <summary>
    /// Надсилає запит (шлях — відносно базової адреси) і повертає успішну відповідь; інакше — <see cref="ConnectorException"/>.
    /// Ним користуються й згенеровані tools (<see cref="OData.ODataTool"/>).
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
            throw new ConnectorException(secrets.Scrub($"OData 1С недоступний: {ex.Message}"));
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
                $"OData {request.Method} {request.RequestUri}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                (body.Length > 0 ? $" — {body}" : "") +
                (response.StatusCode == HttpStatusCode.Unauthorized ? $" Перевірте {ConnectorSecrets.UsernameVariable} і {ConnectorSecrets.PasswordVariable}." : "")));
        }
    }
}
