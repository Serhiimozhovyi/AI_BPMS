using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.KitCrm.Tests;

/// <summary>Запит до OData у <see cref="FakeKit"/>.</summary>
/// <param name="Method">Метод.</param>
/// <param name="Target">Шлях із query, декодований.</param>
/// <param name="Authorization">Заголовок <c>Authorization</c>.</param>
/// <param name="FrontPagePath">Заголовок <c>FrontPagePath</c>.</param>
/// <param name="Body">Тіло.</param>
public sealed record KitRequest(string Method, string Target, string? Authorization, string? FrontPagePath, string Body);

/// <summary>
/// In-process фейк Keycloak (<c>/realms/astarta/protocol/openid-connect/token</c>) і OData Кіта (<c>/odata/…</c>) у
/// пам'яті: таблиці з PascalCase-полями, GET колекції з простим <c>$filter</c> (<c>eq</c> і групи <c>or</c>; умову
/// <c>IsDelete</c> фейк навмисно ігнорує, щоб перевірити відсікання на боці конектора), GET <c>/Set/{id}</c>, POST
/// (вкладені cross-таблиці теж зберігаються), PATCH <c>?key=</c>.
/// </summary>
public sealed class FakeKit : IAsyncDisposable
{
    /// <summary>Клієнт і секрет, які фейк вважає правильними.</summary>
    public const string ClientId = "openbp-kit", ClientSecret = "s3cr3t~kit-client-secret";

    private readonly WebApplication _app;
    private int _issued;
    private string? _valid;

    private FakeKit(WebApplication app) => _app = app;

    /// <summary>Таблиці: назва набору → рядки.</summary>
    public ConcurrentDictionary<string, List<JsonObject>> Tables { get; } = new();

    /// <summary>Запити до OData.</summary>
    public ConcurrentQueue<KitRequest> Requests { get; } = new();

    /// <summary>Скільки токенів видано.</summary>
    public int TokensIssued => _issued;

    /// <summary>Запис без цього <c>FrontPagePath</c> → 403 (як <c>RolePermissionHandler</c> для ролі без CRM_user).</summary>
    public string? RequiredFrontPagePath { get; set; }

    /// <summary>OData відповідає 401 на будь-який токен.</summary>
    public bool RejectAllTokens { get; set; }

    /// <summary>Адреса з кінцевим «/».</summary>
    public Uri Address => new(_app.Urls.Single() + "/");

    /// <summary>Базова адреса OData.</summary>
    public Uri ODataUrl => new(Address, "odata/");

    /// <summary>Token endpoint Keycloak.</summary>
    public Uri TokenUrl => new(Address, "realms/astarta/protocol/openid-connect/token");

    /// <summary>Чинний токен перестає прийматися.</summary>
    public void RevokeTokens() => _valid = null;

    /// <summary>Додає рядок у таблицю (можна й під час запитів — тести стрічки подій).</summary>
    public void Add(string set, object row)
    {
        var rows = Rows(set);
        lock (rows)
        {
            rows.Add(JsonSerializer.SerializeToNode(row)!.AsObject());
        }
    }

    private List<JsonObject> Snapshot(string set)
    {
        var rows = Rows(set);
        lock (rows)
        {
            return [.. rows];
        }
    }

    /// <summary>Рядки таблиці.</summary>
    public List<JsonObject> Rows(string set) => Tables.GetOrAdd(set, _ => []);

    public static async Task<FakeKit> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeKit(app);
        app.MapPost("/realms/astarta/protocol/openid-connect/token", fake.TokenAsync);
        app.Map("/odata/{**path}", fake.ODataAsync);
        await app.StartAsync();
        return fake;
    }

    private async Task TokenAsync(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret || form["grant_type"] != "client_credentials")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            // Справжній Keycloak секрет не повертає, але фейк «відлунює» його, щоб перевірити очищення.
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = $"Invalid client secret {form["client_secret"]}" });
            return;
        }

        var token = $"tok-{Interlocked.Increment(ref _issued)}";
        _valid = token;
        await ctx.Response.WriteAsJsonAsync(new { token_type = "Bearer", expires_in = 300, access_token = token });
    }

    private async Task ODataAsync(HttpContext ctx, string path)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var authorization = ctx.Request.Headers.Authorization.FirstOrDefault();
        var frontPagePath = ctx.Request.Headers["FrontPagePath"].FirstOrDefault();
        var target = Uri.UnescapeDataString(ctx.Features.Get<IHttpRequestFeature>()!.RawTarget);
        Requests.Enqueue(new KitRequest(ctx.Request.Method, target, authorization, frontPagePath, body));
        if (RejectAllTokens || _valid is null || authorization != $"Bearer {_valid}")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var method = ctx.Request.Method;
        if (method != "GET" && RequiredFrontPagePath is { } required && frontPagePath != required)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var segments = path.Split('/');
        var set = segments[0];
        switch (method)
        {
            case "GET" when segments.Length == 2:
                var one = Rows(set).FirstOrDefault(r => Same(r["Id"], segments[1]));
                if (one is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await ctx.Response.WriteAsync(one.ToJsonString());
                return;
            case "GET":
                var filter = ctx.Request.Query["$filter"].FirstOrDefault();
                var rows = Snapshot(set).Where(r => Matches(r, filter)).ToList();
                if (int.TryParse(ctx.Request.Query["$top"].FirstOrDefault(), out var top))
                {
                    rows = [.. rows.Take(top)];
                }

                await ctx.Response.WriteAsync(new JsonObject { ["value"] = new JsonArray([.. rows.Select(r => (JsonNode)r.DeepClone())]) }.ToJsonString());
                return;
            case "POST":
                var row = JsonNode.Parse(body)!.AsObject();
                if (row["Id"] is null || row["Name"] is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = new { message = "The Name field is required." } });
                    return;
                }

                foreach (var cross in row.Where(p => p.Value is JsonArray).Select(p => p.Key).ToList())
                {
                    foreach (var item in row[cross]!.AsArray())
                    {
                        Rows(cross).Add(item!.DeepClone().AsObject());
                    }
                }

                row["Created"] = "2026-10-24T09:00:00Z";
                Rows(set).Add(row);
                ctx.Response.StatusCode = StatusCodes.Status201Created;
                await ctx.Response.WriteAsync(row.ToJsonString());
                return;
            case "PATCH":
                var key = ctx.Request.Query["key"].FirstOrDefault();
                var existing = Rows(set).FirstOrDefault(r => Same(r["Id"], key));
                if (existing is not null)
                {
                    foreach (var (name, value) in JsonNode.Parse(body)!.AsObject())
                    {
                        existing[name] = value?.DeepClone();
                    }

                    existing["Updated"] = "2026-10-24T10:00:00Z";
                }

                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
        }
    }

    private static bool Same(JsonNode? value, string? text) => string.Equals(value?.ToString(), text, StringComparison.OrdinalIgnoreCase);

    /// <summary>Кожна умова <c>A eq b</c> чи група <c>(A eq b or A eq c)</c>; інші умови й <c>IsDelete</c> ігноруються.</summary>
    private static bool Matches(JsonObject row, string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            return true;
        }

        foreach (var clause in filter.Split(" and "))
        {
            var alternatives = clause.Trim('(', ')').Split(" or ").Select(a => a.Split(" eq ")).Where(a => a.Length == 2).ToList();
            if (alternatives.Count == 0 || alternatives.Any(a => a[0] == "IsDelete") || alternatives.Any(a => a[0].Contains('(')))
            {
                continue;
            }

            if (!alternatives.Any(a => Same(row[a[0]], a[1].Trim('\''))))
            {
                return false;
            }
        }

        return true;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
