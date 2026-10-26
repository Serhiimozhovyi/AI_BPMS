using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenBP.Connectors.GSheets;

/// <summary>
/// Типізований <see cref="HttpClient"/> до Google Sheets API з токеном від <see cref="TokenProvider"/>. 401 → новий токен і
/// один повтор; інша неуспішна відповідь → <see cref="ConnectorException"/> зі статусом, <c>error.status</c>/<c>message</c>
/// Google, підказкою для 403/404 і <c>Retry-After</c> для 429/503 — без ключа й токена.
/// </summary>
/// <param name="http">Клієнт (базова адреса — <see cref="ConnectorOptions.SheetsUrl"/>).</param>
/// <param name="tokens">Токени.</param>
/// <param name="secrets">Ключ (для підказки з email сервісного акаунта) і очищення текстів помилок.</param>
public sealed class SheetsClient(HttpClient http, TokenProvider tokens, ConnectorSecrets secrets)
{
    /// <summary>Скільки символів тіла показувати, якщо це не помилка у форматі Google.</summary>
    public const int ErrorBodyLimit = 300;

    /// <summary>Запит до Sheets API.</summary>
    /// <param name="method">Метод.</param>
    /// <param name="path">Шлях із query відносно <see cref="ConnectorOptions.SheetsUrl"/>, напр. <c>spreadsheets/{id}/values:batchGet?…</c>.</param>
    /// <param name="body">Тіло JSON або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>JSON відповіді (порожня — порожній об'єкт).</returns>
    /// <exception cref="ConnectorException">Помилка Sheets, сервера токенів чи мережі.</exception>
    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
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
                throw new ConnectorException(secrets.Scrub($"Google Sheets API недоступний: {ex.Message}"));
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

                return string.IsNullOrWhiteSpace(text) ? JsonSerializer.SerializeToElement(new { }) : JsonSerializer.Deserialize<JsonElement>(text);
            }
        }
    }

    private string Describe(HttpMethod method, string path, HttpResponseMessage response, string text)
    {
        string? detail = null;
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var status = error.TryGetProperty("status", out var s) ? s.GetString() : null;
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                detail = status is { Length: > 0 } ? $"{status}: {message}" : message;
            }
        }
        catch (JsonException)
        {
            // Не помилка у форматі Google.
        }

        detail ??= text.Length > ErrorBodyLimit ? text[..ErrorBodyLimit] + "…" : text;
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => $" Перевірте id таблиці й те, що таблицю відкрито для {Email()} (Поділитися → редактор).",
            _ => "",
        };
        var retry = response.Headers.RetryAfter is { } after
            ? $" Повторіть через {(after.Delta?.TotalSeconds ?? Math.Max(0, (after.Date!.Value - DateTimeOffset.UtcNow).TotalSeconds)):0} с (Retry-After)."
            : "";
        var resource = Uri.UnescapeDataString(path.Split('?', 2)[0]);
        return $"Sheets {method} {resource}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
               (detail is { Length: > 0 } ? $" — {detail}." : ".") + hint + retry;
    }

    private string Email()
    {
        try
        {
            return secrets.Key().ClientEmail!;
        }
        catch (ConnectorException)
        {
            return "сервісного акаунта";
        }
    }
}
