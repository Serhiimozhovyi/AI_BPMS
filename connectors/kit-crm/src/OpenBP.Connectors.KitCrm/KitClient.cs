using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenBP.Connectors.KitCrm;

/// <summary>
/// Типізований <see cref="HttpClient"/> до OData Кіта (базова адреса — <see cref="ConnectorOptions.BaseUrl"/>) з токеном
/// Keycloak від <see cref="TokenProvider"/> (docs/kit-sales.md §5.1–5.2):
/// <list type="bullet">
/// <item>на запис (POST/PATCH/PUT/DELETE) — заголовок <c>FrontPagePath</c>, якщо задано <c>KIT_FRONT_PAGE_PATH</c>;</item>
/// <item>401 → новий токен і один повтор; другий 401 — «сервісний користувач не автентифікований»;</item>
/// <item>403 на запис — підказка про роль <c>CRM_user</c> і <c>KIT_FRONT_PAGE_PATH</c>;</item>
/// <item>інша неуспішна відповідь → <see cref="ConnectorException"/> зі статусом і початком тіла — без секрету й токена.</item>
/// </list>
/// </summary>
/// <param name="http">Клієнт.</param>
/// <param name="tokens">Токени.</param>
/// <param name="secrets">Для <c>FrontPagePath</c> і очищення текстів помилок.</param>
public sealed class KitClient(HttpClient http, TokenProvider tokens, ConnectorSecrets secrets)
{
    /// <summary>Заголовок, який перевіряє <c>RolePermissionHandler</c> Кіта на запис.</summary>
    public const string FrontPagePathHeader = "FrontPagePath";

    /// <summary>Скільки символів тіла помилки показувати.</summary>
    public const int ErrorBodyLimit = 300;

    /// <summary><c>GET</c> з JSON-відповіддю.</summary>
    /// <param name="path">Шлях із query відносно базової адреси, напр. <c>CrmSales(5)</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>JSON або <c>null</c>, якщо запису немає (404).</returns>
    public async Task<JsonElement?> GetAsync(string path, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, path, null, ct, notFoundIsNull: true).ConfigureAwait(false);

    /// <summary>Запит до OData.</summary>
    /// <param name="method">Метод.</param>
    /// <param name="path">Шлях із query відносно базової адреси.</param>
    /// <param name="body">Тіло JSON або <c>null</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="notFoundIsNull">404 → <c>null</c> замість помилки.</param>
    /// <returns>JSON відповіді або <c>null</c> для порожньої (204).</returns>
    /// <exception cref="ConnectorException">Помилка Кіта, Keycloak чи мережі.</exception>
    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFoundIsNull = false)
    {
        var write = method != HttpMethod.Get;
        for (var attempt = 1; ; attempt++)
        {
            var token = await tokens.GetAsync(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (write && secrets.FrontPagePath is { } frontPagePath)
            {
                request.Headers.TryAddWithoutValidation(FrontPagePathHeader, frontPagePath);
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
                throw new ConnectorException(secrets.Scrub($"Кіт недоступний: {ex.Message}"));
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    tokens.Invalidate(token);
                    if (attempt == 1)
                    {
                        // Токен відкликано чи прострочено раніше, ніж казав expires_in: один повтор з новим.
                        continue;
                    }

                    throw new ConnectorException(
                        "Кіт не прийняв токен: сервісний користувач не автентифікований. Перевірте client у Keycloak (KIT_CLIENT_ID) і що для його service account є користувач у Кіті.");
                }

                if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ConnectorException(secrets.Scrub(Describe(method, path, response, text, write)));
                }

                return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<JsonElement>(text);
            }
        }
    }

    private string Describe(HttpMethod method, string path, HttpResponseMessage response, string text, bool write)
    {
        var detail = ErrorMessage(text);
        var message = $"Кіт: {method} {path} — HTTP {(int)response.StatusCode} {response.ReasonPhrase}" + (detail.Length > 0 ? $": {detail}" : "") + ".";
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            message += write
                ? $" Сервісному користувачу потрібна роль CRM_user у Кіті{(secrets.FrontPagePath is null ? $"; якщо Кіт вимагає сторінку — задайте {ConnectorSecrets.FrontPagePathVariable}" : $"; перевірте значення {ConnectorSecrets.FrontPagePathVariable}")}."
                : " Сервісному користувачу бракує прав у Кіті (роль CRM_user).";
        }

        return message;
    }

    /// <summary><c>error.message</c> OData або початок тіла.</summary>
    private static string ErrorMessage(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? "";
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
