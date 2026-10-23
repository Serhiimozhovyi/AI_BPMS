using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.Telegram;

/// <summary>
/// Типізований <see cref="HttpClient"/> до Telegram Bot API: <c>POST bot&lt;token&gt;/&lt;method&gt;</c>, відповідь
/// <c>{ "ok": true, "result": … }</c> → <c>result</c>. Помилка (<c>ok: false</c>, 4xx/5xx, мережа) →
/// <see cref="ConnectorException"/> з HTTP-статусом, <c>description</c> і <c>retry_after</c> для 429 — без токена.
/// </summary>
/// <param name="http">Клієнт (базова адреса — <see cref="ConnectorOptions.BaseUrl"/>).</param>
/// <param name="secrets">Токен бота.</param>
public sealed class BotApiClient(HttpClient http, ConnectorSecrets secrets)
{
    /// <summary>Скільки символів тіла показувати, якщо це не відповідь Bot API.</summary>
    public const int ErrorBodyLimit = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Метод Bot API з JSON-параметрами.</summary>
    /// <typeparam name="T">Тип <c>result</c>.</typeparam>
    /// <param name="method">Назва методу, напр. <c>sendMessage</c>.</param>
    /// <param name="parameters">Параметри (назви — як у Bot API, snake_case; <c>null</c> не надсилаються).</param>
    /// <param name="ct">Скасування.</param>
    public Task<T> CallAsync<T>(string method, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct) =>
        SendAsync<T>(method, JsonContent.Create(parameters.Where(p => p.Value is not null).ToDictionary(), options: Json), ct);

    /// <summary>Метод Bot API з <c>multipart/form-data</c> (завантаження файлу).</summary>
    /// <typeparam name="T">Тип <c>result</c>.</typeparam>
    /// <param name="method">Назва методу.</param>
    /// <param name="content">Тіло.</param>
    /// <param name="ct">Скасування.</param>
    public Task<T> CallAsync<T>(string method, MultipartFormDataContent content, CancellationToken ct) => SendAsync<T>(method, content, ct);

    private async Task<T> SendAsync<T>(string method, HttpContent content, CancellationToken ct)
    {
        // «./» обов'язковий: токен має вигляд 123456:ABC…, і без нього «bot123456:» читався б як схема URI.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"./bot{secrets.BotToken}/{method}", UriKind.Relative)) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(secrets.Scrub($"Telegram Bot API недоступний: {ex.Message}"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            BotResponse<T>? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<BotResponse<T>>(body, Json);
            }
            catch (JsonException)
            {
                // Не відповідь Bot API (напр. HTML проксі) — покажемо початок тіла.
            }

            if (response.IsSuccessStatusCode && parsed is { Ok: true, Result: { } result })
            {
                return result;
            }

            var description = parsed?.Description ?? (body.Length > ErrorBodyLimit ? body[..ErrorBodyLimit] + "…" : body);
            var retry = parsed?.Parameters?.RetryAfter is { } seconds ? $" Повторіть через {seconds} с (retry_after)." : "";
            throw new ConnectorException(secrets.Scrub(
                $"Telegram {method}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}" +
                (description.Length > 0 ? $" — {description}." : ".") + retry));
        }
    }

    private sealed record BotResponse<T>(
        bool Ok,
        T? Result,
        string? Description,
        [property: JsonPropertyName("error_code")] int? ErrorCode,
        ResponseParameters? Parameters);

    private sealed record ResponseParameters([property: JsonPropertyName("retry_after")] int? RetryAfter);
}
