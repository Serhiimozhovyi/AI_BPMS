using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenBP.Cli.Tests;

/// <summary>
/// In-process фейк Keycloak (<c>POST /token</c>) і OData Кіта (<c>/odata/…</c>) для прогону справжнього конектора
/// <c>kit-crm</c>: таблиці в пам'яті з PascalCase-полями; GET колекції фільтрує лише умови <c>A eq b</c> і групи
/// <c>or</c> (решта, зокрема <c>gt</c>, ігнорується), GET <c>/Set/{id}</c>, POST (вкладені cross-таблиці теж
/// зберігаються), PATCH <c>?key=</c>. <see cref="CloseTaskOnCheck"/> закриває задачу («Виконане») на N-ту перевірку.
/// </summary>
public sealed class FakeKitOData : IDisposable
{
    /// <summary>Client і секрет, які фейк приймає.</summary>
    public const string ClientId = "openbp-kit", ClientSecret = "kit-secret-for-tests";

    /// <summary>Статус «Виконане» (засіяний Кітом).</summary>
    public const string DoneStatus = "90be5dbc-b701-4d8e-9f89-44f340e724cd";

    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private readonly ConcurrentDictionary<string, int> _checks = new();

    public FakeKitOData()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        Address = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Address);
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Адреса з кінцевим «/».</summary>
    public string Address { get; }

    /// <summary>Таблиці: набір → рядки.</summary>
    public ConcurrentDictionary<string, List<JsonObject>> Tables { get; } = new();

    /// <summary>Закрити задачу на цю перевірку <c>get_task</c> (1 — перша); <c>null</c> — ніколи.</summary>
    public int? CloseTaskOnCheck { get; set; }

    /// <summary>Скільки разів читали кожну задачу.</summary>
    public IReadOnlyDictionary<string, int> TaskChecks => _checks;

    /// <summary>Рядки таблиці.</summary>
    public List<JsonObject> Rows(string set) => Tables.GetOrAdd(set, _ => []);

    /// <summary>Додає рядок.</summary>
    public void Add(string set, JsonObject row)
    {
        lock (Tables)
        {
            Rows(set).Add(row);
        }
    }

    private async Task LoopAsync()
    {
        try
        {
            while (true)
            {
                var context = await _listener.GetContextAsync();
                try
                {
                    await HandleAsync(context);
                }
                catch (Exception ex)
                {
                    await WriteAsync(context, 500, $"{{\"error\":{{\"message\":\"{ex.GetType().Name}\"}}}}");
                }
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
            // слухача зупинено
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var path = request.Url!.AbsolutePath;
        if (path == "/token")
        {
            var form = System.Web.HttpUtility.ParseQueryString(body);
            var ok = form["client_id"] == ClientId && form["client_secret"] == ClientSecret && form["grant_type"] == "client_credentials";
            await WriteAsync(context, ok ? 200 : 401, ok ? "{\"access_token\":\"kit-token\",\"expires_in\":300,\"token_type\":\"Bearer\"}" : "{\"error\":\"unauthorized_client\"}");
            return;
        }

        if (!path.StartsWith("/odata/", StringComparison.Ordinal) || request.Headers["Authorization"] != "Bearer kit-token")
        {
            await WriteAsync(context, 401, "");
            return;
        }

        var segments = path["/odata/".Length..].Split('/');
        var set = segments[0];
        lock (Tables)
        {
            switch (request.HttpMethod)
            {
                case "GET" when segments.Length == 2:
                    var one = Rows(set).FirstOrDefault(r => Same(r["Id"], segments[1]));
                    if (one is not null && set == "CrmTasks")
                    {
                        var checks = _checks.AddOrUpdate(segments[1], 1, (_, n) => n + 1);
                        if (checks == CloseTaskOnCheck)
                        {
                            one["CrmTaskStatusId"] = DoneStatus;
                            one["Result"] = "Клієнт підтвердив обсяг";
                        }
                    }

                    Respond(context, one is null ? 404 : 200, one?.ToJsonString() ?? "");
                    return;
                case "GET":
                    var filter = request.QueryString["$filter"];
                    var rows = new JsonArray([.. Rows(set).Where(r => Matches(r, filter)).Select(r => (JsonNode)r.DeepClone())]);
                    Respond(context, 200, new JsonObject { ["value"] = rows }.ToJsonString());
                    return;
                case "POST":
                    var row = JsonNode.Parse(body)!.AsObject();
                    foreach (var cross in row.Where(p => p.Value is JsonArray).Select(p => p.Key).ToList())
                    {
                        foreach (var item in row[cross]!.AsArray())
                        {
                            Rows(cross).Add(item!.DeepClone().AsObject());
                        }
                    }

                    row["Created"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                    Rows(set).Add(row);
                    Respond(context, 201, row.ToJsonString());
                    return;
                case "PATCH":
                    var existing = Rows(set).FirstOrDefault(r => Same(r["Id"], request.QueryString["key"]));
                    foreach (var (name, value) in existing is null ? [] : JsonNode.Parse(body)!.AsObject().ToList())
                    {
                        existing![name] = value?.DeepClone();
                    }

                    Respond(context, 204, "");
                    return;
                default:
                    Respond(context, 405, "");
                    return;
            }
        }
    }

    private static bool Same(JsonNode? value, string? text) => string.Equals(value?.ToString(), text, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(JsonObject row, string? filter)
    {
        foreach (var clause in (filter ?? "").Split(" and ", StringSplitOptions.RemoveEmptyEntries))
        {
            var alternatives = clause.Trim('(', ')').Split(" or ").Select(a => a.Split(" eq ")).Where(a => a.Length == 2 && !a[0].Contains('(')).ToList();
            if (alternatives.Count > 0 && !alternatives.Any(a => Same(row[a[0]], a[1].Trim('\''))))
            {
                return false;
            }
        }

        return true;
    }

    private static void Respond(HttpListenerContext context, int status, string json) =>
        WriteAsync(context, status, json).GetAwaiter().GetResult();

    private static async Task WriteAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _listener.Close();
        _loop.Wait(TimeSpan.FromSeconds(5));
    }
}
