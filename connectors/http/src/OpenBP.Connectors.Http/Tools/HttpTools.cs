using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Web;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.Http.Tools;

/// <summary>
/// Tools конектора: читання RSS/Atom і довільний HTTP-запит. Описи — англійською (їх читають ШІ-агенти).
/// <see cref="ConnectorException"/> → результат з <c>isError</c>.
/// </summary>
/// <param name="web">HTTP-клієнт з перевіркою адрес.</param>
[McpServerToolType]
public sealed class HttpTools(WebClient web)
{
    /// <summary>Записів за замовчуванням у <c>rss_read</c>.</summary>
    public const int DefaultFeedLimit = 50;

    /// <summary>Найбільше записів у <c>rss_read</c>.</summary>
    public const int MaxFeedLimit = 500;

    /// <summary>Найбільший таймаут запиту, секунд.</summary>
    public const int MaxTimeoutSec = 300;

    private const int DefaultTimeoutSec = 30;
    private const int ErrorBodyLimit = 300;

    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD"];

    /// <summary><c>rss_read</c>.</summary>
    /// <param name="url">Адреса стрічки.</param>
    /// <param name="limit">Скільки записів повернути.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "rss_read", Title = "Read RSS/Atom feed", UseStructuredContent = true, ReadOnly = true, OpenWorld = true)]
    [Description("Reads an RSS 2.0, RSS 1.0 or Atom feed and returns its entries (title, link, isoDate, pubDate, contentSnippet, content, creator, categories, guid) in feed order, newest first as the feed lists them. In an AI_BPMS mcp.tool node set itemsPath to \"items\" to get one item per entry.")]
    public Task<RssResult> RssRead(
        [Description("Feed URL, http or https.")] string url,
        [Description("Maximum number of entries, 1-500 (default 50).")] int? limit = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var max = limit ?? DefaultFeedLimit;
            if (max is < 1 or > MaxFeedLimit)
            {
                throw new ConnectorException($"limit — число від 1 до {MaxFeedLimit}.");
            }

            var response = await web.SendAsync(
                HttpMethod.Get, ParseUrl(url, null), new Dictionary<string, string> { ["accept"] = "application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.5" },
                null, TimeSpan.FromSeconds(DefaultTimeoutSec), ct).ConfigureAwait(false);
            if (!response.Ok)
            {
                throw new ConnectorException($"Стрічка {WebClient.Safe(response.FinalUri)}: HTTP {response.Status} {response.Reason}.");
            }

            var feed = RssReader.Parse(Decode(response), max);
            return new RssResult(
                feed.FeedTitle,
                feed.FeedLink,
                [.. feed.Items.Select(i => new RssItemResult(i.Title, i.Link, i.IsoDate, i.PubDate, i.ContentSnippet, i.Content, i.Creator, i.Categories, i.Guid))]);
        });

    /// <summary><c>http_request</c>.</summary>
    /// <param name="url">Адреса.</param>
    /// <param name="method">Метод.</param>
    /// <param name="headers">Заголовки.</param>
    /// <param name="query">Параметри запиту.</param>
    /// <param name="json">JSON-тіло.</param>
    /// <param name="body">Текстове тіло.</param>
    /// <param name="timeoutSec">Таймаут.</param>
    /// <param name="fail_on_status_error">Чи вважати статус не 2xx помилкою.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "http_request", Title = "HTTP request", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Sends an HTTP request and returns { status, ok, reason, url, headers, contentType } plus the body as json (JSON responses), text (text responses) or base64 (binary responses). Private, loopback and cloud-metadata addresses are blocked unless the connector allows the host. By default a non-2xx status is an error; pass fail_on_status_error=false to get the status and body instead.")]
    public Task<HttpResult> HttpRequest(
        [Description("Request URL, http or https.")] string url,
        [Description("GET (default), POST, PUT, PATCH, DELETE or HEAD.")] string? method = null,
        [Description("Optional request headers, an object of strings, e.g. { \"Authorization\": \"Bearer …\" }.")] JsonElement? headers = null,
        [Description("Optional query parameters, an object; values are appended to the URL (arrays repeat the key).")] JsonElement? query = null,
        [Description("Optional JSON body (any JSON value); sent with Content-Type application/json unless you set another Content-Type header. Do not combine with body.")] JsonElement? json = null,
        [Description("Optional raw text body (sent as text/plain unless a Content-Type header is set). Do not combine with json.")] string? body = null,
        [Description("Timeout for the whole request in seconds, 1-300 (default 30).")] int? timeoutSec = null,
        [Description("true (default): HTTP status outside 2xx is an error. false: return it as a normal result.")] bool? fail_on_status_error = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var verb = (method ?? "GET").Trim().ToUpperInvariant();
            if (!Methods.Contains(verb, StringComparer.Ordinal))
            {
                throw new ConnectorException($"method — один з {string.Join(", ", Methods)}.");
            }

            var timeout = timeoutSec ?? DefaultTimeoutSec;
            if (timeout is < 1 or > MaxTimeoutSec)
            {
                throw new ConnectorException($"timeoutSec — число від 1 до {MaxTimeoutSec}.");
            }

            var hasJson = json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
            if (hasJson && body is not null)
            {
                throw new ConnectorException("Задайте або json, або body, не обидва.");
            }

            var requestHeaders = Headers(headers);
            Func<HttpContent>? content = null;
            if (hasJson)
            {
                var text = json!.Value.GetRawText();
                content = () => new StringContent(text, Encoding.UTF8, "application/json");
            }
            else if (body is not null)
            {
                content = () => new StringContent(body, Encoding.UTF8, "text/plain");
            }

            if (content is not null && verb is "GET" or "HEAD")
            {
                throw new ConnectorException($"Метод {verb} не має тіла: приберіть json/body або змініть метод.");
            }

            var response = await web.SendAsync(
                new HttpMethod(verb), ParseUrl(url, query), requestHeaders, content, TimeSpan.FromSeconds(timeout), ct).ConfigureAwait(false);
            if (!response.Ok && fail_on_status_error != false)
            {
                var snippet = Snippet(response);
                throw new ConnectorException($"{verb} {WebClient.Safe(response.FinalUri)}: HTTP {response.Status} {response.Reason}" + (snippet.Length > 0 ? $" — {snippet}" : "."));
            }

            return ToResult(response);
        });

    private static HttpResult ToResult(WebResponse response)
    {
        var type = response.ContentType ?? "";
        var common = new HttpResult(response.Status, response.Ok, response.Reason, WebClient.Safe(response.FinalUri), response.Headers, response.ContentType);
        if (response.Body.Length == 0)
        {
            return common;
        }

        var isText = type.StartsWith("text/", StringComparison.Ordinal) || type.Contains("json", StringComparison.Ordinal) || type.Contains("xml", StringComparison.Ordinal)
                     || type.Contains("javascript", StringComparison.Ordinal) || type.Contains("x-www-form-urlencoded", StringComparison.Ordinal);
        if (!isText && type.Length > 0)
        {
            return common with { Base64 = Convert.ToBase64String(response.Body) };
        }

        var text = Decode(response);
        if (type.Contains("json", StringComparison.Ordinal))
        {
            try
            {
                return common with { Json = JsonSerializer.Deserialize<JsonElement>(text) };
            }
            catch (JsonException)
            {
                // Заявлено JSON, але тіло не JSON — віддаємо текстом.
            }
        }

        return common with { Text = text };
    }

    private static string Decode(WebResponse response) => (response.CharsetEncoding ?? Encoding.UTF8).GetString(response.Body);

    private static string Snippet(WebResponse response)
    {
        var text = response.ContentType is { } type && (type.StartsWith("text/", StringComparison.Ordinal) || type.Contains("json", StringComparison.Ordinal) || type.Contains("xml", StringComparison.Ordinal))
            ? Decode(response).Trim()
            : "";
        return text.Length > ErrorBodyLimit ? text[..ErrorBodyLimit] + "…" : text;
    }

    private static Uri ParseUrl(string url, JsonElement? query)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new ConnectorException("url — абсолютна адреса http або https.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ConnectorException("Логін і пароль в адресі не підтримуються: передайте їх заголовком Authorization.");
        }

        var parameters = new List<string>();
        foreach (var (name, value) in Object(query, "query"))
        {
            foreach (var item in value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value])
            {
                if (item.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    continue;
                }

                var text = item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText();
                parameters.Add($"{HttpUtility.UrlEncode(name)}={HttpUtility.UrlEncode(text)}");
            }
        }

        if (parameters.Count == 0)
        {
            return uri;
        }

        var builder = new UriBuilder(uri);
        builder.Query = string.Join('&', builder.Query.TrimStart('?').Length > 0 ? [builder.Query.TrimStart('?'), .. parameters] : parameters);
        return builder.Uri;
    }

    private static Dictionary<string, string> Headers(JsonElement? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Object(headers, "headers"))
        {
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase) || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.String)
            {
                throw new ConnectorException($"Значення заголовка «{name}» має бути рядком.");
            }

            result[name] = value.GetString()!;
        }

        return result;
    }

    /// <summary>Необов'язковий аргумент-об'єкт; рядок з JSON-об'єктом розбирається (ШІ-агенти іноді передають так).</summary>
    private static IEnumerable<KeyValuePair<string, JsonElement>> Object(JsonElement? value, string name)
    {
        if (value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            try
            {
                element = JsonSerializer.Deserialize<JsonElement>(element.GetString()!);
            }
            catch (JsonException)
            {
                throw new ConnectorException($"{name} — JSON-об'єкт.");
            }
        }

        return element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value)).ToList()
            : throw new ConnectorException($"{name} — JSON-об'єкт.");
    }

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
