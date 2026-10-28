using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenBP.Connectors.OData;

/// <summary>
/// Типізований <see cref="HttpClient"/> до сервісу OData (базова адреса — <see cref="ConnectorOptions.BaseUrl"/>) із
/// заголовком від <see cref="AuthProvider"/>:
/// <list type="bullet">
/// <item>401 → якщо токен перевидається, новий токен і один повтор; інакше (або вдруге) — «облікові дані не прийнято»;</item>
/// <item>400/403/404/інші → <see cref="ConnectorException"/> людським текстом з <c>error.message</c> OData, без секретів;</item>
/// <item>посилання <c>@odata.nextLink</c> іде лише на той самий хост, що й сервіс: токен не відправляється нікуди більше.</item>
/// </list>
/// </summary>
/// <param name="http">Клієнт.</param>
/// <param name="auth">Заголовок входу.</param>
/// <param name="secrets">Для очищення текстів помилок.</param>
public sealed class ODataClient(HttpClient http, AuthProvider auth, ConnectorSecrets secrets)
{
    /// <summary>Скільки символів тіла помилки показувати.</summary>
    public const int ErrorBodyLimit = 300;

    /// <summary>Адреса сервісу (з кінцевим «/»).</summary>
    public Uri ServiceUrl => http.BaseAddress!;

    /// <summary><c>GET</c> з JSON-відповіддю.</summary>
    /// <param name="path">Шлях із query відносно адреси сервісу або посилання <c>nextLink</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="notFoundIsNull">404 → <c>null</c> замість помилки.</param>
    public Task<JsonElement?> GetAsync(string path, CancellationToken ct, bool notFoundIsNull = false) =>
        SendAsync(HttpMethod.Get, path, null, ct, notFoundIsNull);

    /// <summary><c>GET</c> тексту (напр. <c>$metadata</c>, XML).</summary>
    /// <param name="path">Шлях відносно адреси сервісу.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        var (text, _) = await SendCoreAsync(HttpMethod.Get, path, null, "application/xml", ct, false).ConfigureAwait(false);
        return text ?? "";
    }

    /// <summary>Запит до OData.</summary>
    /// <param name="method">Метод.</param>
    /// <param name="path">Шлях із query відносно адреси сервісу або посилання <c>nextLink</c>.</param>
    /// <param name="body">Тіло JSON або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="notFoundIsNull">404 → <c>null</c> замість помилки.</param>
    /// <returns>JSON відповіді або <c>null</c> для порожньої (204) чи неіснуючого запису.</returns>
    /// <exception cref="ConnectorException">Помилка сервісу чи мережі.</exception>
    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFoundIsNull = false)
    {
        var (text, _) = await SendCoreAsync(method, path, body, "application/json", ct, notFoundIsNull).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<(string? Text, HttpStatusCode Status)> SendCoreAsync(HttpMethod method, string path, object? body, string accept, CancellationToken ct, bool notFoundIsNull)
    {
        var target = Resolve(path);
        for (var attempt = 1; ; attempt++)
        {
            var header = await auth.GetAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, target);
            request.Headers.Authorization = header;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (method != HttpMethod.Get)
            {
                request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
            }

            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ConnectorException(secrets.Scrub($"Сервіс OData недоступний: {ex.Message}"));
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (auth.Invalidate(header) && attempt == 1)
                    {
                        // Токен відкликано чи прострочено раніше, ніж казав expires_in: один повтор з новим.
                        continue;
                    }

                    throw new ConnectorException("Сервіс не прийняв облікові дані (401). Перевірте облікові дані підключення: Client ID і секрет, токен або ім'я й пароль.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull)
                {
                    return (null, response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ConnectorException(secrets.Scrub(Describe(method, path, response, text)));
                }

                return (text, response.StatusCode);
            }
        }
    }

    /// <summary>Відносний шлях — від адреси сервісу; абсолютне посилання — лише на той самий хост.</summary>
    private Uri Resolve(string path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var absolute) || absolute.Scheme is not ("http" or "https"))
        {
            return new Uri(path, UriKind.Relative);
        }

        if (Uri.Compare(absolute, ServiceUrl, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new ConnectorException("Сервіс повернув посилання на інший хост; облікові дані туди не надсилаються.");
        }

        return absolute;
    }

    private static string Describe(HttpMethod method, string path, HttpResponseMessage response, string text)
    {
        var detail = ErrorMessage(text);
        var suffix = detail.Length > 0 ? $": {detail}" : "";
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => $"Сервіс відхилив запит (400){suffix}. Перевірте $filter, $select, $expand та імена полів.",
            HttpStatusCode.Forbidden => $"Недостатньо прав (403){suffix}. Користувачу з облікових даних бракує доступу до цих даних.",
            HttpStatusCode.NotFound => $"Не знайдено (404): {Short(path)}{suffix}.",
            _ => $"OData: {method} {Short(path)} — HTTP {(int)response.StatusCode} {response.ReasonPhrase}{suffix}.",
        };
    }

    private static string Short(string path) => path.Length > 120 ? path[..120] + "…" : path;

    /// <summary><c>error.message</c> OData (v4 — рядок, v2 — <c>message.value</c>) або початок тіла.</summary>
    private static string ErrorMessage(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message))
            {
                var value = message.ValueKind switch
                {
                    JsonValueKind.String => message.GetString(),
                    JsonValueKind.Object when message.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
            // Не JSON — нижче початок тіла.
        }

        var trimmed = text.Trim();
        return trimmed.Length > ErrorBodyLimit ? trimmed[..ErrorBodyLimit] + "…" : trimmed;
    }
}
