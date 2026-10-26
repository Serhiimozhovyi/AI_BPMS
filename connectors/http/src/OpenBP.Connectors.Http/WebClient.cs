using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace OpenBP.Connectors.Http;

/// <summary>Відповідь сервера: статус, заголовки (імена в нижньому регістрі) і тіло.</summary>
/// <param name="Status">HTTP-статус.</param>
/// <param name="Reason">Пояснення статусу.</param>
/// <param name="FinalUri">Адреса після редіректів.</param>
/// <param name="Headers">Заголовки відповіді.</param>
/// <param name="ContentType">Тип вмісту без параметрів, нижнім регістром.</param>
/// <param name="CharsetEncoding">Кодування з <c>Content-Type</c>, якщо задано й відоме.</param>
/// <param name="Body">Тіло.</param>
public sealed record WebResponse(
    int Status,
    string Reason,
    Uri FinalUri,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    System.Text.Encoding? CharsetEncoding,
    byte[] Body)
{
    /// <summary>Успішний статус (2xx).</summary>
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>
/// HTTP-клієнт конектора: редіректи вручну (до <see cref="MaxRedirects"/>, кожен перевіряється <see cref="HostPolicy"/>),
/// тіло з обмеженням розміру, таймаут на запит. Адреса підключення перевіряється в <see cref="Connect"/>.
/// </summary>
/// <param name="http">Клієнт без автоматичних редіректів і з вимкненим таймаутом.</param>
/// <param name="policy">Які адреси дозволено.</param>
/// <param name="options">Налаштування (ліміт розміру).</param>
public sealed class WebClient(HttpClient http, HostPolicy policy, IOptions<ConnectorOptions> options)
{
    /// <summary>Найбільше редіректів за один запит.</summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// Підключення зі сокетом, у який перетворено хост: IP перевіряється <see cref="HostPolicy.IsAddressAllowed"/> безпосередньо
    /// перед з'єднанням (<c>SocketsHttpHandler.ConnectCallback</c>).
    /// </summary>
    /// <param name="policy">Які адреси дозволено.</param>
    /// <param name="context">Контекст підключення.</param>
    /// <param name="ct">Скасування.</param>
    public static async ValueTask<Stream> Connect(HostPolicy policy, SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var allowed = addresses.Where(a => policy.IsAddressAllowed(host, a)).ToArray();
        if (allowed.Length == 0)
        {
            throw new ConnectorException(
                $"Хост «{host}» вказує на приватну адресу, а такі запити заборонені. Дозвольте його змінною {ConnectorSettings.AllowPrivateVariable}.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Виконує запит і читає відповідь.</summary>
    /// <param name="method">Метод.</param>
    /// <param name="uri">Адреса.</param>
    /// <param name="headers">Заголовки запиту.</param>
    /// <param name="content">Створює тіло (потрібне заново на редіректі 307/308); <c>null</c> — без тіла.</param>
    /// <param name="timeout">Час на весь запит разом з редіректами й читанням тіла.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConnectorException">Адреса заборонена, таймаут, мережа, розмір відповіді.</exception>
    public async Task<WebResponse> SendAsync(
        HttpMethod method, Uri uri, IReadOnlyDictionary<string, string> headers, Func<HttpContent>? content, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var current = uri;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                policy.Check(current);
                using var request = new HttpRequestMessage(method, current);
                foreach (var (name, value) in headers)
                {
                    // Заголовки вмісту (Content-Type) не підуть у Headers запиту — вони додаються до тіла нижче.
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                if (content is not null)
                {
                    request.Content = content();
                    foreach (var (name, value) in headers.Where(h => IsContentHeader(h.Key)))
                    {
                        request.Content.Headers.Remove(name);
                        request.Content.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                    && response.Headers.Location is { } location)
                {
                    if (redirects >= MaxRedirects)
                    {
                        throw new ConnectorException($"Забагато редіректів (понад {MaxRedirects}) від {Safe(uri)}.");
                    }

                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (response.StatusCode is HttpStatusCode.SeeOther
                        || (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && method == HttpMethod.Post))
                    {
                        method = HttpMethod.Get;
                        content = null;
                    }

                    if (!next.Host.Equals(current.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        headers = headers.Where(h => !h.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                                                     && !h.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase)).ToDictionary(h => h.Key, h => h.Value);
                    }

                    current = next;
                    continue;
                }

                return await ReadAsync(response, current, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException($"Таймаут {timeout.TotalSeconds:0.#} с: {Safe(current)} не відповів.");
        }
        catch (HttpRequestException ex)
        {
            throw Wrap(ex, current);
        }
    }

    /// <summary>Адреса без параметрів запиту й облікових даних: у них часто ключі API.</summary>
    /// <param name="uri">Адреса.</param>
    public static string Safe(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    private static bool IsContentHeader(string name) =>
        name.Equals("content-type", StringComparison.OrdinalIgnoreCase) || name.Equals("content-language", StringComparison.OrdinalIgnoreCase);

    private static ConnectorException Wrap(HttpRequestException ex, Uri uri)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is ConnectorException known)
            {
                return known;
            }
        }

        var cause = ex.InnerException?.Message ?? ex.Message;
        return new ConnectorException($"Запит до {Safe(uri)} не вдався: {cause}");
    }

    private async Task<WebResponse> ReadAsync(HttpResponseMessage response, Uri uri, CancellationToken ct)
    {
        var limit = options.Value.MaxResponseBytes;
        if (response.Content.Headers.ContentLength > limit)
        {
            throw new ConnectorException($"Відповідь {Safe(uri)} більша за ліміт {limit} байт.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new ConnectorException($"Відповідь {Safe(uri)} більша за ліміт {limit} байт.");
            }

            buffer.Write(chunk, 0, read);
        }

        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value), StringComparer.Ordinal);
        var contentType = response.Content.Headers.ContentType;
        return new WebResponse(
            (int)response.StatusCode,
            response.ReasonPhrase ?? "",
            uri,
            headers,
            contentType?.MediaType?.ToLowerInvariant(),
            Encoding(contentType?.CharSet),
            buffer.ToArray());
    }

    private static System.Text.Encoding? Encoding(string? charset)
    {
        try
        {
            return string.IsNullOrWhiteSpace(charset) ? null : System.Text.Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
