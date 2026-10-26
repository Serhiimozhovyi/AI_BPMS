using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.GSheets.Tests;

/// <summary>Запит до Sheets у <see cref="FakeSheets"/>: <see cref="Target"/> — URL як надіслано.</summary>
/// <param name="Method">Метод.</param>
/// <param name="Target">Шлях із query без декодування.</param>
/// <param name="Authorization">Заголовок <c>Authorization</c>.</param>
/// <param name="Body">Тіло.</param>
public sealed record SheetsRequest(string Method, string Target, string? Authorization, string Body);

/// <summary>
/// In-process фейк сервера токенів Google (<c>/token</c>: перевіряє підпис JWT відкритим ключем сервісного акаунта, <c>iss</c>,
/// <c>aud</c>, <c>scope</c>) і Sheets API v4 (<c>/v4/…</c>: <c>values:batchGet</c>, <c>values/{range}</c>,
/// <c>values:batchUpdate</c>, <c>values/{range}:append</c>) над таблицею в пам'яті з id <see cref="SpreadsheetId"/>.
/// Видає токени <c>tok-1</c>, <c>tok-2</c>…, приймає лише останній виданий; <see cref="FailWith"/> — помилка на все.
/// </summary>
public sealed class FakeSheets : IAsyncDisposable
{
    /// <summary>Id таблиці, яку знає фейк.</summary>
    public const string SpreadsheetId = "1AbC-sheet_id";

    /// <summary>Email сервісного акаунта.</summary>
    public const string ClientEmail = "openbp-bot@openbp-test.iam.gserviceaccount.com";

    private readonly WebApplication _app;
    private readonly RSA _rsa = RSA.Create(2048);
    private int _issued;
    private string? _valid;

    private FakeSheets(WebApplication app) => _app = app;

    /// <summary>Листи таблиці: рядки клітинок (JSON-значення), як їх зберігає Google.</summary>
    public Dictionary<string, List<List<JsonNode?>>> Sheets { get; } = new(StringComparer.Ordinal);

    /// <summary>Запити до Sheets.</summary>
    public ConcurrentQueue<SheetsRequest> Requests { get; } = new();

    /// <summary>Скільки токенів видано.</summary>
    public int TokensIssued => _issued;

    /// <summary>Якщо задано — кожна відповідь Sheets має цей статус, тіло й <c>Retry-After</c>.</summary>
    public (int Status, string Body, int? RetryAfter)? FailWith { get; set; }

    /// <summary>Sheets відповідає 401 на будь-який токен.</summary>
    public bool RejectAllTokens { get; set; }

    /// <summary>Адреса з кінцевим «/».</summary>
    public Uri Address => new(_app.Urls.Single() + "/");

    /// <summary>Базова адреса Sheets API.</summary>
    public Uri SheetsUrl => new(Address, "v4/");

    /// <summary>Закритий ключ сервісного акаунта у PEM (PKCS#8).</summary>
    public string PrivateKeyPem => _rsa.ExportPkcs8PrivateKeyPem();

    /// <summary>JSON-ключ сервісного акаунта, як його завантажують з Google Cloud Console (з <c>token_uri</c> фейку).</summary>
    public string KeyJson(string? privateKey = null) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account",
        ["project_id"] = "openbp-test",
        ["private_key_id"] = "kid-1",
        ["private_key"] = privateKey ?? PrivateKeyPem,
        ["client_email"] = ClientEmail,
        ["client_id"] = "1234567890",
        ["token_uri"] = new Uri(Address, "token").AbsoluteUri,
    });

    /// <summary>Чинний токен перестає прийматися (як після відкликання).</summary>
    public void RevokeTokens() => _valid = null;

    /// <summary>Значення клітинки рядком (для перевірок).</summary>
    public string? Cell(string sheet, int row, int column) =>
        Sheets[sheet] is var rows && row - 1 < rows.Count && column < rows[row - 1].Count ? rows[row - 1][column]?.ToString() : null;

    public static async Task<FakeSheets> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeSheets(app);
        app.MapPost("/token", fake.TokenAsync);
        app.Map("/v4/{**path}", fake.SheetsAsync);
        await app.StartAsync();
        return fake;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _rsa.Dispose();
    }

    private async Task TokenAsync(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        var error = form["grant_type"] != "urn:ietf:params:oauth:grant-type:jwt-bearer" ? "unsupported_grant_type" : VerifyJwt(form["assertion"].ToString());
        if (error is not null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            // Справжній Google асерцію не повертає, але фейк «відлунює» її, щоб перевірити очищення.
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = $"{error}: {form["assertion"]}" });
            return;
        }

        var token = $"tok-{Interlocked.Increment(ref _issued)}";
        _valid = token;
        await ctx.Response.WriteAsJsonAsync(new { access_token = token, expires_in = 3599, token_type = "Bearer" });
    }

    private string? VerifyJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return "Invalid JWT";
        }

        var header = JsonNode.Parse(Base64Url(parts[0]))!;
        var claims = JsonNode.Parse(Base64Url(parts[1]))!;
        if ((string?)header["alg"] != "RS256" || (string?)header["kid"] != "kid-1")
        {
            return "Invalid JWT header";
        }

        if (!_rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return "Invalid JWT Signature";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (string?)claims["iss"] != ClientEmail || (string?)claims["aud"] != new Uri(Address, "token").AbsoluteUri ||
               (string?)claims["scope"] != "https://www.googleapis.com/auth/spreadsheets" ||
               (long)claims["iat"]! > now + 5 || (long)claims["exp"]! - (long)claims["iat"]! != 3600
            ? "Invalid JWT claims"
            : null;
    }

    private static byte[] Base64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    private async Task SheetsAsync(HttpContext ctx, string path)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var authorization = ctx.Request.Headers.Authorization.FirstOrDefault();
        Requests.Enqueue(new SheetsRequest(ctx.Request.Method, ctx.Features.Get<IHttpRequestFeature>()!.RawTarget, authorization, body));
        if (RejectAllTokens || _valid is null || authorization != $"Bearer {_valid}")
        {
            await ErrorAsync(ctx, 401, "UNAUTHENTICATED", $"Request had invalid authentication credentials: {authorization}");
            return;
        }

        if (FailWith is { } fail)
        {
            ctx.Response.StatusCode = fail.Status;
            if (fail.RetryAfter is { } seconds)
            {
                ctx.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await ctx.Response.WriteAsync(fail.Body);
            return;
        }

        var decoded = Uri.UnescapeDataString(ctx.Request.Path.Value!)["/v4/spreadsheets/".Length..];
        var slash = decoded.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || decoded[..slash] != SpreadsheetId)
        {
            await ErrorAsync(ctx, 404, "NOT_FOUND", "Requested entity was not found.");
            return;
        }

        var rest = decoded[(slash + 1)..];
        try
        {
            object result = (ctx.Request.Method, rest) switch
            {
                ("GET", "values:batchGet") => new
                {
                    spreadsheetId = SpreadsheetId,
                    valueRanges = ctx.Request.Query["ranges"].Select(r => Read(r!)).ToArray(),
                },
                ("POST", "values:batchUpdate") => BatchUpdate(JsonNode.Parse(body)!),
                ("POST", _) when rest.StartsWith("values/", StringComparison.Ordinal) && rest.EndsWith(":append", StringComparison.Ordinal) =>
                    Append(rest["values/".Length..^":append".Length], JsonNode.Parse(body)!),
                ("GET", _) when rest.StartsWith("values/", StringComparison.Ordinal) => Read(rest["values/".Length..]),
                _ => throw new InvalidOperationException($"Unsupported {ctx.Request.Method} {rest}"),
            };
            await ctx.Response.WriteAsJsonAsync(result);
        }
        catch (KeyNotFoundException ex)
        {
            await ErrorAsync(ctx, 400, "INVALID_ARGUMENT", $"Unable to parse range: {ex.Message}");
        }
    }

    private static Task ErrorAsync(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error = new { code = status, message, status = code } });
    }

    /// <summary>Розбирає <c>'Лист'!A1:B2</c>, <c>Лист</c> чи <c>'Лист'!1:1</c> → лист, перший рядок і чи лише рядок 1.</summary>
    private (List<List<JsonNode?>> Rows, string Sheet, string? Cells) Range(string range)
    {
        var bang = range.LastIndexOf('!');
        var name = bang < 0 ? range : range[..bang];
        if (name.StartsWith('\'') && name.EndsWith('\''))
        {
            name = name[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }

        return Sheets.TryGetValue(name, out var rows) ? (rows, name, bang < 0 ? null : range[(bang + 1)..]) : throw new KeyNotFoundException(range);
    }

    private object Read(string range)
    {
        var (rows, sheet, cells) = Range(range);
        var selected = cells == "1:1" ? rows.Take(1) : rows;
        var values = selected.Select(r => Trim(r).Select(c => c?.DeepClone()).ToList()).ToList();
        while (values.Count > 0 && values[^1].Count == 0)
        {
            values.RemoveAt(values.Count - 1);
        }

        return values.Count == 0
            ? new Dictionary<string, object> { ["range"] = $"'{sheet}'!A1:Z1000", ["majorDimension"] = "ROWS" }
            : new Dictionary<string, object> { ["range"] = $"'{sheet}'!A1:Z1000", ["majorDimension"] = "ROWS", ["values"] = values };
    }

    private object BatchUpdate(JsonNode request)
    {
        var updated = 0;
        foreach (var data in request["data"]!.AsArray())
        {
            var (rows, _, cells) = Range((string)data!["range"]!);
            var match = System.Text.RegularExpressions.Regex.Match(cells!, @"^([A-Z]+)(\d+)$");
            var column = match.Groups[1].Value.Aggregate(0, (n, ch) => (n * 26) + (ch - 'A' + 1)) - 1;
            var row = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            while (rows.Count < row)
            {
                rows.Add([]);
            }

            while (rows[row - 1].Count <= column)
            {
                rows[row - 1].Add(null);
            }

            rows[row - 1][column] = data["values"]![0]![0]?.DeepClone();
            updated++;
        }

        return new { spreadsheetId = SpreadsheetId, totalUpdatedCells = updated };
    }

    private object Append(string range, JsonNode request)
    {
        var (rows, sheet, _) = Range(range);
        while (rows.Count > 0 && Trim(rows[^1]).Count == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var first = rows.Count + 1;
        var added = request["values"]!.AsArray().Select(r => r!.AsArray().Select(c => c?.DeepClone()).ToList()).ToList();
        rows.AddRange(added);
        var width = added.Max(r => r.Count);
        var last = (char)('A' + width - 1);
        return new
        {
            spreadsheetId = SpreadsheetId,
            tableRange = $"'{sheet}'!A1:{last}{first - 1}",
            updates = new { updatedRange = $"'{sheet}'!A{first}:{last}{rows.Count}", updatedRows = added.Count },
        };
    }

    private static List<JsonNode?> Trim(List<JsonNode?> row)
    {
        var count = row.Count;
        while (count > 0 && (row[count - 1] is null || row[count - 1]!.ToString() == ""))
        {
            count--;
        }

        return row.Take(count).ToList();
    }
}
