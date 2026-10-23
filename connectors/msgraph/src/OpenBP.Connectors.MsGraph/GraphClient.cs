using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenBP.Connectors.MsGraph;

/// <summary>
/// Типізований <see cref="HttpClient"/> до Microsoft Graph з токеном від <see cref="TokenProvider"/>. 401 → новий токен і
/// один повтор; інша неуспішна відповідь → <see cref="ConnectorException"/> зі статусом, <c>error.code</c>/<c>message</c>
/// Graph і <c>Retry-After</c> для 429/503 — без секрету й токена.
/// </summary>
/// <param name="http">Клієнт (базова адреса — <see cref="ConnectorOptions.GraphUrl"/>).</param>
/// <param name="tokens">Токени.</param>
/// <param name="secrets">Для очищення текстів помилок.</param>
public sealed class GraphClient(HttpClient http, TokenProvider tokens, ConnectorSecrets secrets)
{
    /// <summary>Скільки символів тіла показувати, якщо це не помилка у форматі Graph.</summary>
    public const int ErrorBodyLimit = 300;

    /// <summary>Запит до Graph.</summary>
    /// <param name="method">Метод.</param>
    /// <param name="path">Шлях із query відносно <see cref="ConnectorOptions.GraphUrl"/>, напр. <c>users/a@b.c/messages</c>.</param>
    /// <param name="body">Тіло JSON або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>JSON відповіді або <c>null</c> для порожньої (202, 204).</returns>
    /// <exception cref="ConnectorException">Помилка Graph, Entra ID чи мережі.</exception>
    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await tokens.GetAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ConnectorException(secrets.Scrub($"Microsoft Graph недоступний: {ex.Message}"));
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    // Токен відкликано чи прострочено раніше, ніж казав expires_in: один повтор з новим.
                    tokens.Invalidate(token);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ConnectorException(secrets.Scrub(Describe(method, path, response, text)));
                }

                return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<JsonElement>(text);
            }
        }
    }

    private static string Describe(HttpMethod method, string path, HttpResponseMessage response, string text)
    {
        string? detail = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                detail = $"{error.GetProperty("code").GetString()}: {(error.TryGetProperty("message", out var m) ? m.GetString() : "")}";
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Не помилка у форматі Graph.
        }

        detail ??= text.Length > ErrorBodyLimit ? text[..ErrorBodyLimit] + "…" : text;
        var retry = response.Headers.RetryAfter is { } after
            ? $" Повторіть через {(after.Delta?.TotalSeconds ?? Math.Max(0, (after.Date!.Value - DateTimeOffset.UtcNow).TotalSeconds)):0} с (Retry-After)."
            : "";
        var resource = path.Split('?', 2)[0];
        return $"Graph {method} {resource}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
               (detail.Length > 0 ? $" — {detail}." : ".") + retry;
    }
}
