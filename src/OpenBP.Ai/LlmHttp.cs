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
            throw new LlmException(StatusMessage((int)response.StatusCode)) { Details = details };
        }

        return text;
    }

    /// <summary>Що сталося і що зробити за HTTP-статусом провайдера (F3-57); тіло відповіді — у Details.</summary>
    internal static string StatusMessage(int status) => status switch
    {
        401 or 403 => $"Провайдер моделі відхилив ключ доступу (HTTP {status}). Перевірте API-ключ у credential вузла.",
        404 => $"Провайдер не знайшов модель або адресу (HTTP {status}). Перевірте назву моделі і baseUrl.",
        429 => $"Провайдер обмежив кількість запитів (HTTP {status}). Повторіть пізніше або налаштуйте повтори вузла.",
        >= 500 => $"Провайдер моделі тимчасово недоступний (HTTP {status}). Повторіть запуск пізніше.",
        _ => $"Провайдер моделі відхилив запит (HTTP {status}). Перевірте модель і параметри вузла.",
    };
}
