using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBP.Cli.Tests;

/// <summary>Збирає справжній конектор <c>connectors/telegram</c> один раз на клас тестів.</summary>
public sealed class TelegramFixture : IDisposable
{
    private static readonly Lock BuildLock = new();

    public TelegramFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("openbp-tg-").FullName;
        var output = Path.Combine(Directory, "telegram");
        var project = Path.Combine(KitCrmFixture.RepoRoot, "connectors", "telegram", "src", "OpenBP.Connectors.Telegram", "OpenBP.Connectors.Telegram.csproj");
        var start = new ProcessStartInfo(KitCrmFixture.DotNet)
        {
            ArgumentList = { "build", project, "-o", output, "-nologo", "-v", "q", "-p:UseSharedCompilation=false" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        lock (BuildLock)
        {
            using var process = Process.Start(start)!;
            var log = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Збірка telegram: {log.Result}{errors.Result}");
        }

        Dll = Path.Combine(output, "OpenBP.Connectors.Telegram.dll");
    }

    /// <summary>Тимчасова тека.</summary>
    public string Directory { get; }

    /// <summary>Зібраний <c>OpenBP.Connectors.Telegram.dll</c>.</summary>
    public string Dll { get; }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}

/// <summary>
/// In-process фейк Telegram Bot API для погодження: запам'ятовує запити, на <see cref="PressOnCheck"/>-те читання
/// <c>getUpdates</c> «натискає» кнопку останнього <c>sendMessage</c> з кнопками погодження.
/// </summary>
public sealed class FakeTelegramApprovals : IDisposable
{
    public const string Token = "777:approval-test-SECRET";

    private readonly HttpListener _listener = new();
    private readonly Task _loop;
    private int _checks;
    private string? _token;
    private long _messageId;
    private long _approvalMessageId;

    public FakeTelegramApprovals()
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

    public string Address { get; }

    /// <summary>На яке читання <c>getUpdates</c> натиснути (1 — перше); <c>null</c> — ніколи.</summary>
    public int? PressOnCheck { get; set; }

    /// <summary>Яку кнопку натиснути.</summary>
    public bool Approve { get; set; } = true;

    /// <summary>Запити: метод і JSON-тіло.</summary>
    public ConcurrentQueue<(string Method, JsonObject Body)> Requests { get; } = new();

    public int Checks => _checks;

    public IEnumerable<JsonObject> Sent(string method) => Requests.Where(r => r.Method == method).Select(r => r.Body);

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Слухача зупинено.
        }
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = JsonNode.Parse(await reader.ReadToEndAsync()) as JsonObject ?? [];
            var path = ctx.Request.Url!.AbsolutePath.Trim('/').Split('/');
            object? result;
            if (path.Length != 2 || path[0] != $"bot{Token}")
            {
                await WriteAsync(ctx, 401, new { ok = false, error_code = 401, description = "Unauthorized" });
                continue;
            }

            var method = path[1];
            Requests.Enqueue((method, body));
            switch (method)
            {
                case "sendMessage":
                    var data = body["reply_markup"]?["inline_keyboard"]?[0]?[0]?["callback_data"]?.GetValue<string>();
                    if (data is not null && data.StartsWith("apv:", StringComparison.Ordinal))
                    {
                        _token = data.Split(':')[1];
                    }

                    var id = Interlocked.Increment(ref _messageId) + 900;
                    if (data is not null && data.StartsWith("apv:", StringComparison.Ordinal))
                    {
                        _approvalMessageId = id;
                    }

                    result = new { message_id = id, date = 1761200000, chat = new { id = 42, type = "private" }, text = body["text"]?.GetValue<string>() };
                    break;
                case "getUpdates":
                    var check = Interlocked.Increment(ref _checks);
                    result = PressOnCheck is { } n && check >= n && _token is not null
                        ? new object[]
                        {
                            new
                            {
                                update_id = 5000,
                                callback_query = new
                                {
                                    id = "cb-approval",
                                    from = new { id = 501, is_bot = false, first_name = "Олена", username = "olena" },
                                    data = $"apv:{_token}:{(Approve ? 1 : 0)}",
                                    message = new { message_id = _approvalMessageId, date = 1761200000, chat = new { id = 42, type = "private" }, text = "Погодити пост?" },
                                },
                            },
                        }
                        : Array.Empty<object>();
                    break;
                case "answerCallbackQuery":
                    result = true;
                    break;
                case "editMessageText":
                    result = new { message_id = _approvalMessageId, date = 1761200000, chat = new { id = 42, type = "private" }, text = body["text"]?.GetValue<string>() };
                    break;
                default:
                    await WriteAsync(ctx, 404, new { ok = false, error_code = 404, description = "Not Found" });
                    continue;
            }

            await WriteAsync(ctx, 200, new { ok = true, result });
        }
    }

    private static async Task WriteAsync(HttpListenerContext ctx, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }
}

/// <summary>
/// F6-27: сценарій <c>send_approval → mcp.waitFor get_approval → core.if approved</c> через CLI зі справжнім конектором
/// Telegram проти фейку Bot API; CLI перемотує очікування між перевірками.
/// </summary>
public sealed class TelegramApprovalTests(TelegramFixture telegram) : IClassFixture<TelegramFixture>, IDisposable
{
    private const string Workflow = """
        {
          "formatVersion": 1,
          "name": "Погодження поста в Telegram",
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_ask", "name": "Запит на погодження", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
              "parameters": { "serverId": "srv_telegram", "toolName": "send_approval",
                "arguments": { "chat_id": 42, "text": "Погодити пост?" } } },
            { "id": "n_wait", "name": "Чекати відповідь", "type": "mcp.waitFor", "typeVersion": 1, "position": [400, 0],
              "parameters": { "serverId": "srv_telegram", "toolName": "get_approval",
                "arguments": { "chat_id": "{{ $json.chat_id }}", "message_id": "{{ $json.message_id }}", "token": "{{ $json.token }}" },
                "conditions": [ { "field": "answered", "operator": "isTrue" } ], "intervalMinutes": 5, "timeoutHours": 24 } },
            { "id": "n_if", "name": "Погоджено?", "type": "core.if", "typeVersion": 1, "position": [600, -100],
              "parameters": { "conditions": [ { "left": "{{ $json.approved }}", "operator": "isTrue" } ], "combine": "and" } },
            { "id": "n_post", "name": "Публікація", "type": "mcp.tool", "typeVersion": 1, "position": [800, -200],
              "parameters": { "serverId": "srv_telegram", "toolName": "send_message", "arguments": { "chat_id": 42, "text": "Опубліковано, погодив(ла) {{ $json.by.first_name }}" } } },
            { "id": "n_rejected", "name": "Відхилено", "type": "mcp.tool", "typeVersion": 1, "position": [800, 0],
              "parameters": { "serverId": "srv_telegram", "toolName": "send_message", "arguments": { "chat_id": 42, "text": "Пост відхилено" } } },
            { "id": "n_timeout", "name": "Без відповіді", "type": "mcp.tool", "typeVersion": 1, "position": [600, 200],
              "parameters": { "serverId": "srv_telegram", "toolName": "send_message", "arguments": { "chat_id": 42, "text": "Ніхто не відповів за добу" } } }
          ],
          "connections": [
            { "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_ask", "input": 0 } },
            { "from": { "node": "n_ask", "output": 0 }, "to": { "node": "n_wait", "input": 0 } },
            { "from": { "node": "n_wait", "output": 0 }, "to": { "node": "n_if", "input": 0 } },
            { "from": { "node": "n_wait", "output": 1 }, "to": { "node": "n_timeout", "input": 0 } },
            { "from": { "node": "n_if", "output": 0 }, "to": { "node": "n_post", "input": 0 } },
            { "from": { "node": "n_if", "output": 1 }, "to": { "node": "n_rejected", "input": 0 } }
          ]
        }
        """;

    private readonly FakeTelegramApprovals _api = new();

    public void Dispose() => _api.Dispose();

    [Fact]
    public async Task Approved_request_publishes_the_post()
    {
        _api.PressOnCheck = 3;

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Equal(3, _api.Checks);
        var texts = _api.Sent("sendMessage").Select(b => b["text"]!.GetValue<string>()).ToList();
        Assert.Equal(["Погодити пост?", "Опубліковано, погодив(ла) Олена"], texts);
        Assert.Single(_api.Sent("answerCallbackQuery"));
        Assert.EndsWith("✅ Погоджено: Олена (@olena)", _api.Sent("editMessageText").Single()["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Очікувань: 2;", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeTelegramApprovals.Token, stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_request_goes_to_the_false_branch()
    {
        _api.PressOnCheck = 1;
        _api.Approve = false;

        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);
        Assert.Equal(1, _api.Checks);
        Assert.Equal("Пост відхилено", _api.Sent("sendMessage").Last()["text"]!.GetValue<string>());
        Assert.Contains("▶ Публікація [mcp.tool] — Skipped", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_answer_within_a_day_goes_to_timeout()
    {
        var (code, stdout, stderr) = await RunAsync();

        Assert.True(code == CliApp.Ok, stdout + stderr);

        // Перша перевірка одразу, далі кожні 5 хв до 24 год: 1 + 288.
        Assert.Equal(289, _api.Checks);
        Assert.Equal("Ніхто не відповів за добу", _api.Sent("sendMessage").Last()["text"]!.GetValue<string>());
        Assert.Empty(_api.Sent("editMessageText"));
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync()
    {
        var file = Path.Combine(telegram.Directory, $"approval-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, Workflow);
        var env = new Dictionary<string, string>
        {
            ["Connector__BaseUrl"] = _api.Address,
            ["TELEGRAM_BOT_TOKEN"] = FakeTelegramApprovals.Token,
            ["Logging__LogLevel__Default"] = "Warning",
        };
        var servers = Path.Combine(telegram.Directory, $"servers-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(servers, JsonSerializer.Serialize(new
        {
            servers = new object[] { new { id = "srv_telegram", transport = "stdio", command = KitCrmFixture.DotNet, args = new[] { telegram.Dll }, env } },
        }));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["run", file, "--servers", servers], stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
