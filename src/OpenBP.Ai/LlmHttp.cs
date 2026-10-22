namespace OpenBP.Ai;

/// <summary>Спільна відправка HTTP-запиту до провайдера з єдиною обробкою помилкових статусів.</summary>
internal static class LlmHttp
{
    private const int MaxErrorBodyChars = 500;

    /// <summary>Надсилає запит і повертає тіло успішної відповіді.</summary>
    /// <exception cref="LlmException">Статус не 2xx (401, 429, 5xx…).</exception>
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage message, CancellationToken ct)
    {
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = text.Length > MaxErrorBodyChars ? text[..MaxErrorBodyChars] + "…" : text;
            throw new LlmException($"Провайдер відповів HTTP {(int)response.StatusCode}: {details}");
        }

        return text;
    }
}
