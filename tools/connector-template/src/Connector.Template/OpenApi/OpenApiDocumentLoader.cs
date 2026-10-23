using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Connector.Template.OpenApi;

/// <summary>Читає OpenAPI 3.x (JSON або YAML) з файлу чи за URL (парсер — Microsoft.OpenApi, MIT).</summary>
public static class OpenApiDocumentLoader
{
    /// <summary>Завантажує й розбирає специфікацію.</summary>
    /// <param name="location">URL <c>http(s)://…</c> або шлях до файлу (відносний — від теки застосунку).</param>
    /// <param name="http">Клієнт для URL (без автентифікації конектора: специфікація може лежати на іншому хості).</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="InvalidOperationException">Специфікацію не вдалося прочитати чи розібрати.</exception>
    public static async Task<OpenApiDocument> LoadAsync(string location, HttpClient http, CancellationToken ct)
    {
        byte[] content;
        string? format;
        if (Uri.TryCreate(location, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps))
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"OpenAPI {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            content = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            format = FormatOf(url.AbsolutePath) ?? FormatOf(response.Content.Headers.ContentType?.MediaType);
        }
        else
        {
            var path = Path.GetFullPath(location, AppContext.BaseDirectory);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"OpenAPI: файл {path} не знайдено.");
            }

            content = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            format = FormatOf(path);
        }

        format ??= Sniff(content);
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        using var stream = new MemoryStream(content);
        var (document, diagnostic) = await OpenApiDocument.LoadAsync(stream, format, settings, ct).ConfigureAwait(false);
        if (document is null || diagnostic?.Errors.Count > 0)
        {
            var errors = diagnostic?.Errors.Take(5).Select(e => $"{e.Pointer} {e.Message}".Trim()) ?? [];
            throw new InvalidOperationException($"OpenAPI {location}: специфікацію не розібрано — {string.Join("; ", errors)}");
        }

        return document;
    }

    private static string? FormatOf(string? pathOrMediaType) => pathOrMediaType?.ToLowerInvariant() switch
    {
        null => null,
        var s when s.EndsWith(".json", StringComparison.Ordinal) || s.EndsWith("json", StringComparison.Ordinal) => OpenApiConstants.Json,
        var s when s.EndsWith(".yaml", StringComparison.Ordinal) || s.EndsWith(".yml", StringComparison.Ordinal) || s.EndsWith("yaml", StringComparison.Ordinal) => OpenApiConstants.Yaml,
        _ => null,
    };

    /// <summary>JSON починається з «{», усе інше вважаємо YAML.</summary>
    private static string Sniff(byte[] content)
    {
        foreach (var b in content)
        {
            if (!char.IsWhiteSpace((char)b) && b != 0xEF && b != 0xBB && b != 0xBF)
            {
                return b == (byte)'{' ? OpenApiConstants.Json : OpenApiConstants.Yaml;
            }
        }

        return OpenApiConstants.Json;
    }
}
