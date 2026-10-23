using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.OneC.Tests;

/// <summary>Запит, який отримав <see cref="MockOneC"/>.</summary>
/// <param name="Method">Метод.</param>
/// <param name="EntitySet">Entity set (декодований).</param>
/// <param name="Target">Шлях із query, як у рядку запиту HTTP.</param>
/// <param name="Filter">Декодований <c>$filter</c>.</param>
/// <param name="Body">Тіло.</param>
public sealed record OneCRequest(string Method, string EntitySet, string Target, string? Filter, JsonElement? Body);

/// <summary>
/// In-process мок OData-публікації 1С (OData v3, <c>/trade/odata/standard.odata/</c>) на випадковому порту: Basic-автентифікація,
/// <c>$metadata</c> з <c>OData/onec.xml</c>, контрагенти, номенклатура, продажі; <c>$filter</c> розбирається лише в тих виразах,
/// які формує конектор. Кожен запит (крім <c>$metadata</c>) запам'ятовується.
/// </summary>
public sealed partial class MockOneC : IAsyncDisposable
{
    public const string Username = "odata.user";
    public const string Password = "1c-p@ss-SECRET";

    public const string Agro = "6f1c2a8e-0d5b-4c33-9a5e-2b7d8f1e4a10";
    public const string Lan = "0b9a7c11-52de-4e0f-8d3a-77c1e2f40a22";
    public const string Corn = "a1111111-1111-4111-8111-111111111111";
    public const string Wheat = "b2222222-2222-4222-8222-222222222222";
    public const string OrderNumber = "ЗП-000123";
    public const string OrderKey = "c3333333-3333-4333-8333-333333333333";

    private static readonly JsonSerializerOptions Json = new();

    private static readonly (string Key, string Name)[] Customers =
    [
        (Agro, "ТОВ Агро"),
        (Lan, "ФГ Лан"),
        ("d4444444-4444-4444-8444-444444444444", "ПП Дубль"),
        ("e5555555-5555-4555-8555-555555555555", "ПП Дубль"),
    ];

    private static readonly (string Key, string Name, string Sku)[] Items =
    [
        (Corn, "Насіння кукурудзи", "A-100"),
        (Wheat, "Насіння пшениці", "B-200"),
        ("f6666666-6666-4666-8666-666666666666", "Добриво 1", "D-1"),
        ("f7777777-7777-4777-8777-777777777777", "Добриво 2", "D-1"),
    ];

    private static readonly (string Number, string Date, string Customer, double Amount)[] Sales =
    [
        ("РН-0001", "2026-10-01T09:30:00", Agro, 1000.5),
        ("РН-0002", "2026-10-15T12:00:00", Lan, 250),
        ("РН-0003", "2026-10-31T18:00:00", Agro, 99),
        ("РН-0004", "2026-11-01T08:00:00", Lan, 7),
    ];

    private readonly WebApplication _app;

    private MockOneC(WebApplication app) => _app = app;

    /// <summary>Запити (крім <c>$metadata</c>).</summary>
    public ConcurrentQueue<OneCRequest> Requests { get; } = new();

    /// <summary>Адреса публікації з кінцевим «/».</summary>
    public Uri BaseUrl => new(_app.Urls.Single() + "/trade/odata/standard.odata/");

    public static async Task<MockOneC> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var mock = new MockOneC(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        return mock;
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        const string prefix = "/trade/odata/standard.odata/";
        var path = ctx.Request.Path.Value!;
        var set = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        if (ctx.Request.Headers.Authorization != expected)
        {
            // Справжня 1С пароль не повертає; тут — щоб перевірити очищення текстів помилок.
            var sent = ctx.Request.Headers.Authorization.ToString();
            var pair = sent.StartsWith("Basic ", StringComparison.Ordinal) ? Encoding.UTF8.GetString(Convert.FromBase64String(sent[6..])) : "";
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync($"Не удалось авторизоваться ({pair})");
            return;
        }

        if (set == "$metadata")
        {
            ctx.Response.ContentType = "application/xml";
            await ctx.Response.SendFileAsync(Path.Combine(AppContext.BaseDirectory, "OData", "onec.xml"));
            return;
        }

        var filter = ctx.Request.Query["$filter"].FirstOrDefault();
        JsonElement? body = null;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Method == "POST")
        {
            body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        }

        Requests.Enqueue(new OneCRequest(ctx.Request.Method, set, ctx.Features.Get<IHttpRequestFeature>()!.RawTarget, filter, body));
        switch (ctx.Request.Method, set)
        {
            case ("GET", "Catalog_Контрагенты"):
                var names = Strings("Description", filter);
                var keys = GuidLiteral().Matches(filter ?? "").Select(m => m.Groups[1].Value).ToHashSet();
                await Value(ctx, Customers.Where(c => names.Contains(c.Name) || keys.Contains(c.Key))
                    .Select(c => new Dictionary<string, object> { ["Ref_Key"] = c.Key, ["Description"] = c.Name }));
                break;
            case ("GET", "Catalog_Номенклатура"):
                var skus = Strings("Артикул", filter);
                await Value(ctx, Items.Where(i => skus.Contains(i.Sku))
                    .Select(i => new Dictionary<string, object> { ["Ref_Key"] = i.Key, ["Description"] = i.Name, ["Артикул"] = i.Sku }));
                break;
            case ("GET", "Document_РеализацияТоваровУслуг"):
                var dates = DateTimeLiteral().Matches(filter ?? "").Select(m => m.Groups[1].Value).ToList();
                await Value(ctx, Sales.Where(s => dates.Count == 2 && string.CompareOrdinal(s.Date, dates[0]) >= 0 && string.CompareOrdinal(s.Date, dates[1]) <= 0)
                    .Select((s, i) => new Dictionary<string, object>
                    {
                        ["Ref_Key"] = $"9{i:D7}-0000-4000-8000-000000000000",
                        ["Number"] = s.Number,
                        ["Date"] = s.Date,
                        ["Контрагент_Key"] = s.Customer,
                        ["СуммаДокумента"] = s.Amount,
                    }));
                break;
            case ("POST", _) when set.StartsWith("Document_", StringComparison.Ordinal):
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                await ctx.Response.WriteAsJsonAsync<object>(new Dictionary<string, object?>
                {
                    ["odata.metadata"] = $"{BaseUrl}$metadata#{set}/@Element",
                    ["Ref_Key"] = OrderKey,
                    ["Number"] = OrderNumber + "  ",
                    ["Date"] = body?.GetProperty("Date").GetString(),
                }, Json);
                break;
            default:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync<object>(new Dictionary<string, object>
                {
                    ["odata.error"] = new { code = "-1", message = new { lang = "ru", value = $"Не найден {set}" } },
                }, Json);
                break;
        }
    }

    /// <summary>Значення порівнянь <c>&lt;field&gt; eq '…'</c> з <c>''</c> → <c>'</c>.</summary>
    private static HashSet<string> Strings(string field, string? filter) =>
        Regex.Matches(filter ?? "", $"{Regex.Escape(field)} eq '((?:[^']|'')*)'")
            .Select(m => m.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    private static Task Value(HttpContext ctx, IEnumerable<Dictionary<string, object>> rows) =>
        ctx.Response.WriteAsJsonAsync<object>(new Dictionary<string, object> { ["odata.metadata"] = "…", ["value"] = rows.ToList() }, Json);

    [GeneratedRegex("guid'([0-9a-f-]{36})'")]
    private static partial Regex GuidLiteral();

    [GeneratedRegex("datetime'([0-9T:-]+)'")]
    private static partial Regex DateTimeLiteral();

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
