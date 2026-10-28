using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.OData.Tests;

/// <summary>Запит до сервісу в <see cref="FakeOData"/>.</summary>
/// <param name="Method">Метод.</param>
/// <param name="Target">Шлях із query, декодований.</param>
/// <param name="Authorization">Заголовок <c>Authorization</c>.</param>
/// <param name="Body">Тіло.</param>
public sealed record ODataRequest(string Method, string Target, string? Authorization, string Body);

/// <summary>
/// In-process фейк сервісу OData v4 (<c>/odata/…</c>) і сервісу токенів (<c>/token</c>): <c>$metadata</c> з трьома наборами
/// (<c>Orders</c> — ціле, <c>Customers</c> — Guid, <c>Products</c> — рядковий ключ), GET колекції з <c>$filter</c>
/// (<c>A eq b</c> через <c>and</c>), <c>$select</c>, <c>$orderby</c>, <c>$top</c> і серверними сторінками
/// (<see cref="PageSize"/> + <c>@odata.nextLink</c>), GET/PATCH/DELETE за ключем, POST. Анотації <c>@odata.*</c> фейк додає
/// навмисно — конектор має їх прибрати.
/// </summary>
public sealed class FakeOData : IAsyncDisposable
{
    /// <summary>Client і секрет, які фейк вважає правильними.</summary>
    public const string ClientId = "openbp-odata", ClientSecret = "s3cr3t~odata-client-secret";

    /// <summary>Ім'я, пароль і токен для Basic/Bearer.</summary>
    public const string Username = "api-user", Password = "p@ss~word", StaticToken = "static~bearer~token";

    private const string Metadata = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
          <edmx:DataServices>
            <Schema Namespace="Trade" xmlns="http://docs.oasis-open.org/odata/ns/edm">
              <EntityType Name="Order">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                <Property Name="Number" Type="Edm.String"/>
                <Property Name="Status" Type="Edm.String"/>
                <Property Name="Amount" Type="Edm.Decimal"/>
                <NavigationProperty Name="Lines" Type="Collection(Trade.Line)"/>
              </EntityType>
              <EntityType Name="Customer">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Guid" Nullable="false"/>
                <Property Name="Name" Type="Edm.String"/>
              </EntityType>
              <EntityType Name="Product">
                <Key><PropertyRef Name="Code"/></Key>
                <Property Name="Code" Type="Edm.String" Nullable="false"/>
                <Property Name="Title" Type="Edm.String"/>
              </EntityType>
              <EntityContainer Name="Default">
                <EntitySet Name="Orders" EntityType="Trade.Order"/>
                <EntitySet Name="Customers" EntityType="Trade.Customer"/>
                <EntitySet Name="Products" EntityType="Trade.Product"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    private static readonly Dictionary<string, string> Keys = new() { ["Orders"] = "Id", ["Customers"] = "Id", ["Products"] = "Code" };

    private readonly WebApplication _app;
    private int _issued;
    private string? _valid;

    private FakeOData(WebApplication app) => _app = app;

    /// <summary>Таблиці: назва набору → рядки.</summary>
    public ConcurrentDictionary<string, List<JsonObject>> Tables { get; } = new();

    /// <summary>Запити до OData (разом із <c>$metadata</c>).</summary>
    public ConcurrentQueue<ODataRequest> Requests { get; } = new();

    /// <summary>Скільки токенів видано.</summary>
    public int TokensIssued => _issued;

    /// <summary>Скільки секунд живе виданий токен.</summary>
    public int ExpiresIn { get; set; } = 300;

    /// <summary>Остання отримана форма запиту токена.</summary>
    public IReadOnlyDictionary<string, string> LastTokenForm { get; private set; } = new Dictionary<string, string>();

    /// <summary>Розмір серверної сторінки колекції; 0 — без сторінок.</summary>
    public int PageSize { get; set; }

    /// <summary>Посилання <c>@odata.nextLink</c> веде на інший хост.</summary>
    public bool ForeignNextLink { get; set; }

    /// <summary>Запис (POST/PATCH/DELETE) → 403.</summary>
    public bool ForbidWrites { get; set; }

    /// <summary>Який заголовок <c>Authorization</c> фейк приймає; <c>null</c> — токен, виданий <c>/token</c>.</summary>
    public string? RequiredAuthorization { get; set; }

    /// <summary>Сервіс без автентифікації.</summary>
    public bool OpenService { get; set; }

    /// <summary>Адреса з кінцевим «/».</summary>
    public Uri Address => new(_app.Urls.Single() + "/");

    /// <summary>Базова адреса OData.</summary>
    public Uri ODataUrl => new(Address, "odata/");

    /// <summary>Адреса видачі токена.</summary>
    public Uri TokenUrl => new(Address, "token");

    /// <summary>Чинний токен перестає прийматися.</summary>
    public void RevokeTokens() => _valid = null;

    /// <summary>Додає рядок у таблицю.</summary>
    public void Add(string set, object row) => Rows(set).Add(JsonSerializer.SerializeToNode(row)!.AsObject());

    /// <summary>Рядки таблиці.</summary>
    public List<JsonObject> Rows(string set) => Tables.GetOrAdd(set, _ => []);

    public static async Task<FakeOData> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeOData(app);
        app.MapPost("/token", fake.TokenAsync);
        app.Map("/odata/{**path}", fake.ODataAsync);
        await app.StartAsync();
        return fake;
    }

    private async Task TokenAsync(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        LastTokenForm = form.ToDictionary(p => p.Key, p => p.Value.ToString());
        if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret || form["grant_type"] != "client_credentials")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            // Справжній сервіс секрет не повертає, але фейк «відлунює» його, щоб перевірити очищення.
            await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized_client", error_description = $"Invalid client secret {form["client_secret"]}" });
            return;
        }

        var token = $"tok-{Interlocked.Increment(ref _issued)}";
        _valid = token;
        await ctx.Response.WriteAsJsonAsync(new { token_type = "Bearer", expires_in = ExpiresIn, access_token = token });
    }

    private async Task ODataAsync(HttpContext ctx, string path)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var body = await reader.ReadToEndAsync();
        var authorization = ctx.Request.Headers.Authorization.FirstOrDefault();
        var target = Uri.UnescapeDataString(ctx.Features.Get<IHttpRequestFeature>()!.RawTarget).Replace("/odata/", "", StringComparison.Ordinal);
        Requests.Enqueue(new ODataRequest(ctx.Request.Method, target, authorization, body));
        var allowed = OpenService || (RequiredAuthorization is { } required ? authorization == required : _valid is not null && authorization == $"Bearer {_valid}");
        if (!allowed)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (path == "$metadata")
        {
            ctx.Response.ContentType = "application/xml";
            await ctx.Response.WriteAsync(Metadata);
            return;
        }

        var method = ctx.Request.Method;
        if (method != "GET" && ForbidWrites)
        {
            await Error(ctx, StatusCodes.Status403Forbidden, "Write access is denied for this user.");
            return;
        }

        var (set, key) = Split(path);
        if (!Keys.ContainsKey(set))
        {
            await Error(ctx, StatusCodes.Status404NotFound, $"Resource not found for the segment '{set}'.");
            return;
        }

        switch (method)
        {
            case "GET" when key is null:
                await CollectionAsync(ctx, set);
                return;
            case "GET":
                var one = Find(set, key);
                if (one is null)
                {
                    await Error(ctx, StatusCodes.Status404NotFound, "Entity not found.");
                    return;
                }

                await Json(ctx, 200, With(one));
                return;
            case "POST":
                var row = JsonNode.Parse(body)!.AsObject();
                if (set == "Orders")
                {
                    if (row["Number"] is null)
                    {
                        await Error(ctx, StatusCodes.Status400BadRequest, "The Number field is required.");
                        return;
                    }

                    row["Id"] ??= Rows(set).Count == 0 ? 1 : Rows(set).Max(r => r["Id"]!.GetValue<int>()) + 1;
                }

                Rows(set).Add(row);
                await Json(ctx, 201, With(row));
                return;
            case "PATCH" when key is not null:
                var existing = Find(set, key);
                if (existing is null)
                {
                    await Error(ctx, StatusCodes.Status404NotFound, "Entity not found.");
                    return;
                }

                foreach (var (name, value) in JsonNode.Parse(body)!.AsObject())
                {
                    existing[name] = value?.DeepClone();
                }

                if (set == "Products")
                {
                    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }

                await Json(ctx, 200, With(existing));
                return;
            case "DELETE" when key is not null:
                var doomed = Find(set, key);
                if (doomed is null)
                {
                    await Error(ctx, StatusCodes.Status404NotFound, "Entity not found.");
                    return;
                }

                Rows(set).Remove(doomed);
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
        }
    }

    private async Task CollectionAsync(HttpContext ctx, string set)
    {
        var query = ctx.Request.Query;
        var rows = Rows(set).ToList();
        if (query["$filter"].FirstOrDefault() is { Length: > 0 } filter)
        {
            foreach (var clause in filter.Split(" and "))
            {
                var parts = clause.Split(" eq ");
                if (parts.Length != 2)
                {
                    await Error(ctx, StatusCodes.Status400BadRequest, $"Unsupported filter: {clause}");
                    return;
                }

                if (parts[0].StartsWith("Bogus", StringComparison.Ordinal))
                {
                    await Error(ctx, StatusCodes.Status400BadRequest, $"Could not find a property named '{parts[0]}' on type 'Trade.{set.TrimEnd('s')}'.");
                    return;
                }

                rows = [.. rows.Where(r => string.Equals(r[parts[0]]?.ToString(), parts[1].Trim('\''), StringComparison.OrdinalIgnoreCase))];
            }
        }

        if (query["$orderby"].FirstOrDefault() is { Length: > 0 } orderby)
        {
            var parts = orderby.Split(' ');
            rows = [.. parts.Length > 1 && parts[1] == "desc" ? rows.OrderByDescending(r => r[parts[0]]?.ToString()) : rows.OrderBy(r => r[parts[0]]?.ToString())];
        }

        var total = int.TryParse(query["$top"].FirstOrDefault(), out var top) ? Math.Min(top, rows.Count) : rows.Count;
        var skip = int.TryParse(query["$skiptoken"].FirstOrDefault(), out var token) ? token : 0;
        var take = PageSize > 0 ? Math.Min(PageSize, total - skip) : total - skip;
        var page = rows.Skip(skip).Take(Math.Max(take, 0)).ToList();
        var select = query["$select"].FirstOrDefault()?.Split(',');
        var result = new JsonObject
        {
            ["@odata.context"] = $"{ctx.Request.Scheme}://{ctx.Request.Host}/odata/$metadata#{set}",
            ["value"] = new JsonArray([.. page.Select(r => (JsonNode)With(Project(r, select)))]),
        };
        if (skip + take < total)
        {
            var host = ForeignNextLink ? "http://evil.example" : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            result["@odata.nextLink"] = $"{host}/odata/{set}?$top={total}&$skiptoken={skip + take}";
        }

        await Json(ctx, 200, result);
    }

    private static JsonObject Project(JsonObject row, string[]? select) =>
        select is null ? (JsonObject)row.DeepClone() : new JsonObject([.. select.Where(s => row.ContainsKey(s)).Select(s => KeyValuePair.Create(s, row[s]?.DeepClone()))]);

    private static JsonObject With(JsonObject row)
    {
        var copy = (JsonObject)row.DeepClone();
        copy["@odata.etag"] = "W/\"1\"";
        return copy;
    }

    private static (string Set, string? Key) Split(string path)
    {
        var open = path.IndexOf('(');
        return open < 0 ? (path, null) : (path[..open], path[(open + 1)..path.LastIndexOf(')')].Trim('\''));
    }

    private JsonObject? Find(string set, string? key) =>
        Rows(set).FirstOrDefault(r => string.Equals(r[Keys[set]]?.ToString(), key?.Replace("''", "'", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));

    private static async Task Json(HttpContext ctx, int status, JsonNode body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body.ToJsonString());
    }

    private static Task Error(HttpContext ctx, int status, string message) =>
        Json(ctx, status, new JsonObject { ["error"] = new JsonObject { ["code"] = "x", ["message"] = message } });

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
