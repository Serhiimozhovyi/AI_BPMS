using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.MsGraph.Tests;

/// <summary>Запит до Graph у <see cref="FakeGraph"/>: <see cref="Target"/> — URL як надіслано.</summary>
/// <param name="Method">Метод.</param>
/// <param name="Target">Шлях із query без декодування.</param>
/// <param name="Authorization">Заголовок <c>Authorization</c>.</param>
/// <param name="Body">Тіло.</param>
/// <param name="Session">Заголовок <c>workbook-session-id</c>.</param>
public sealed record GraphRequest(string Method, string Target, string? Authorization, string Body, string? Session = null);

/// <summary>
/// In-process фейк Entra ID (<c>/{tenant}/oauth2/v2.0/token</c>) і Graph (<c>/v1.0/…</c>) на випадковому порту: видає
/// токени <c>tok-1</c>, <c>tok-2</c>…, приймає лише останній виданий (<see cref="RevokeTokens"/> робить його чужим),
/// <see cref="FailWith"/> змушує відповіді Graph бути помилкою.
/// </summary>
public sealed class FakeGraph : IAsyncDisposable
{
    /// <summary>Тенант, застосунок і секрет, які фейк вважає правильними.</summary>
    public const string Tenant = "contoso-tenant", ClientId = "app-1", ClientSecret = "s3cr3t~graph-client-secret";

    private readonly WebApplication _app;
    private int _issued;
    private string? _valid;

    private FakeGraph(WebApplication app) => _app = app;

    /// <summary>Книга Excel (F3-32).</summary>
    public FakeWorkbook Workbook { get; } = new();

    /// <summary>Запити до Graph.</summary>
    public ConcurrentQueue<GraphRequest> Requests { get; } = new();

    /// <summary>Скільки токенів видано.</summary>
    public int TokensIssued => _issued;

    /// <summary>Якщо задано — кожна відповідь Graph має цей статус, тіло й <c>Retry-After</c>.</summary>
    public (int Status, string Body, int? RetryAfter)? FailWith { get; set; }

    /// <summary>Graph відповідає 401 на будь-який токен.</summary>
    public bool RejectAllTokens { get; set; }

    /// <summary>Адреса з кінцевим «/».</summary>
    public Uri Address => new(_app.Urls.Single() + "/");

    /// <summary>Базова адреса Graph.</summary>
    public Uri GraphUrl => new(Address, "v1.0/");

    /// <summary>Чинний токен перестає прийматися (як після відкликання).</summary>
    public void RevokeTokens() => _valid = null;

    public static async Task<FakeGraph> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeGraph(app);
        app.MapPost("/{tenant}/oauth2/v2.0/token", fake.TokenAsync);
        app.Map("/v1.0/{**path}", fake.GraphAsync);
        await app.StartAsync();
        return fake;
    }

    private async Task TokenAsync(HttpContext ctx, string tenant)
    {
        var form = await ctx.Request.ReadFormAsync();
        if (tenant != Tenant || form["client_id"] != ClientId || form["client_secret"] != ClientSecret ||
            form["grant_type"] != "client_credentials" || form["scope"] != "https://graph.microsoft.com/.default")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            // Справжній Entra ID секрет не повертає, але фейк «відлунює» його, щоб перевірити очищення.
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "invalid_client",
                error_description = $"AADSTS7000215: Invalid client secret provided: {form["client_secret"]}\r\nTrace ID: 1",
            });
            return;
        }

        var token = $"tok-{Interlocked.Increment(ref _issued)}";
        _valid = token;
        await ctx.Response.WriteAsJsonAsync(new { token_type = "Bearer", expires_in = 3599, access_token = token });
    }

    private async Task GraphAsync(HttpContext ctx, string path)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var authorization = ctx.Request.Headers.Authorization.FirstOrDefault();
        Requests.Enqueue(new GraphRequest(
            ctx.Request.Method, ctx.Features.Get<IHttpRequestFeature>()!.RawTarget, authorization, body, ctx.Request.Headers["workbook-session-id"].FirstOrDefault()));
        if (RejectAllTokens || _valid is null || authorization != $"Bearer {_valid}")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "InvalidAuthenticationToken", message = $"Access token is invalid: {authorization}" } });
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

        var decoded = Uri.UnescapeDataString(ctx.Request.Path.Value!);
        if (await Workbook.HandleAsync(ctx, decoded, body))
        {
            return;
        }

        object? result = (ctx.Request.Method, decoded) switch
        {
            ("GET", "/v1.0/users/orders@contoso.com/mailFolders/inbox/messages") or ("GET", "/v1.0/users/orders@contoso.com/messages") => new
            {
                value = new[]
                {
                    new { id = "AAMk-1", subject = "Замовлення", receivedDateTime = "2026-10-23T06:10:00Z", hasAttachments = true },
                    new { id = "AAMk-2", subject = "Рахунок", receivedDateTime = "2026-10-23T05:00:00Z", hasAttachments = false },
                }.Concat(Delivered()),
            },
            ("GET", "/v1.0/users/orders@contoso.com/messages/AAMk-1") => new
            {
                id = "AAMk-1",
                subject = "Замовлення",
                body = new { contentType = "html", content = "<p>Пшениця 20 т</p>" },
            },
            ("GET", "/v1.0/users/orders@contoso.com/messages/AAMk-1/attachments") => new
            {
                value = new object[]
                {
                    new { id = "att-1", name = "order.csv", contentType = "text/csv", size = 18, isInline = false },
                    new { id = "att-2", name = "Forwarded", contentType = (string?)null, size = 900, isInline = false },
                },
            },
            ("GET", "/v1.0/users/orders@contoso.com/messages/AAMk-1/attachments/att-1") => new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["id"] = "att-1",
                ["name"] = "order.csv",
                ["contentType"] = "text/csv",
                ["contentBytes"] = Convert.ToBase64String("sku;qty\nW-1;20\n"u8.ToArray()),
            },
            ("GET", "/v1.0/users/orders@contoso.com/messages/AAMk-1/attachments/att-2") => new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.itemAttachment",
                ["id"] = "att-2",
                ["name"] = "Forwarded",
            },
            ("POST", "/v1.0/users/reports@contoso.com/sendMail") => "",
            _ => null,
        };
        switch (result)
        {
            case null:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new { error = new { code = "ErrorItemNotFound", message = "The specified object was not found in the store." } });
                break;
            case "":
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
                break;
            default:
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(result));
                break;
        }
    }

    private readonly List<(string Id, string Subject, DateTimeOffset Received)> _delivered = [];

    /// <summary>Новий лист у «Вхідних» (фільтр і сортування запиту фейк не застосовує).</summary>
    public void Deliver(string id, string subject, DateTimeOffset received)
    {
        lock (_delivered)
        {
            _delivered.Add((id, subject, received));
        }
    }

    private IEnumerable<object> Delivered()
    {
        lock (_delivered)
        {
            return [.. _delivered.Select(d => new
            {
                id = d.Id,
                subject = d.Subject,
                receivedDateTime = d.Received.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                hasAttachments = false,
            })];
        }
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
