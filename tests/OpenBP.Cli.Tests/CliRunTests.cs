using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli.Tests;

/// <summary>
/// <c>openbp run</c> на прикладах <c>examples/telegram-1c.json</c> і <c>examples/daily-report.json</c> з моками 1С, Telegram і Graph на FakeServer.
/// Сервери стартують із dll поруч із тестами (а не через <c>dotnet run</c>, як у <c>examples/servers.json</c>).
/// </summary>
public sealed class CliRunTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("openbp-cli-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Example_WithPin_RunsFromWebhookPinDataToTelegramReply()
    {
        var (code, stdout, stderr) = await RunAsync("run", Example, "--pin", "--servers", WriteServers());

        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("▶ Нова заявка [core.webhook] — Success · pinData", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Розібрати заявку [ai.llm] — Success · pinData", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ 1С: створити замовлення [mcp.tool] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("\"number\": 1001", stdout, StringComparison.Ordinal);
        Assert.Contains("\"chatId\": 42", stdout, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"Замовлення №1001 створено\"", stdout, StringComparison.Ordinal);
        Assert.Contains("Результат: Success", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DailyReport_RunsFromScheduleThroughSalesReportToMailWithTable()
    {
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(DailyReport)));
        var kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var before = YesterdayIn(kyiv);

        var (code, stdout, stderr) = await RunAsync("run", DailyReport, "--servers", WriteServers());

        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("▶ Розклад [core.schedule] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Період [core.set] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ 1С: продажі за вчора [mcp.tool] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Таблиця по контрагентах [core.code] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Надіслати звіт [mcp.tool] — Success", stdout, StringComparison.Ordinal);

        // sales_report отримав учорашній день (за Києвом) як from і to.
        var day = Regex.Match(stdout, "\"date\": \"(\\d{4}-\\d{2}-\\d{2})T10:00:00\"").Groups[1].Value;
        Assert.Contains(day, new[] { before, YesterdayIn(kyiv) });

        // Лист: таблиця по контрагентах, відсортована за сумою, з підсумком.
        Assert.Contains("\"sent\": true", stdout, StringComparison.Ordinal);
        Assert.Contains($"\"subject\": \"Продажі за {day}: 1349.50 грн\"", stdout, StringComparison.Ordinal);
        Assert.Contains($"<h3>Продажі за {day}</h3><table", stdout, StringComparison.Ordinal);
        Assert.Contains("<tr><td>ТОВ Агро</td><td>2</td><td>1099.50</td></tr><tr><td>ФГ Лан</td><td>1</td><td>250.00</td></tr>", stdout, StringComparison.Ordinal);
        Assert.Contains("<tr><th>Разом</th><th>3</th><th>1349.50</th></tr>", stdout, StringComparison.Ordinal);
        Assert.Contains("Результат: Success", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrderTotal_GuideExample_ComputesTotalsWithExpressionsAndCode()
    {
        // Приклад з docs/guide/authors.md (вирази, Code eachItem/allItems, core.if): не має розходитися з посібником.
        Assert.Empty(await new WorkflowValidator().ValidateAsync(await File.ReadAllTextAsync(OrderTotal)));

        var (code, stdout, stderr) = await RunAsync("run", OrderTotal);

        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("\"total\": 300", stdout, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"ПЕТРЕНКО: 2 рядки на 1200 грн\"", stdout, StringComparison.Ordinal);
        Assert.Contains("▶ Велике замовлення? [core.if] — Success", stdout, StringComparison.Ordinal);
        Assert.Contains("Результат: Success", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Example_WithoutPin_WebhookNodeFailsWithHint()
    {
        var (code, stdout, _) = await RunAsync("run", Example, "--servers", WriteServers());

        Assert.Equal(CliApp.ExecutionFailed, code);
        Assert.Contains("▶ Нова заявка [core.webhook] — Error", stdout, StringComparison.Ordinal);
        Assert.Contains("/webhook/", stdout, StringComparison.Ordinal);
        Assert.Contains("pinData", stdout, StringComparison.Ordinal);
        Assert.Contains("Результат: Error", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Example_WithoutServers_McpNodeFails()
    {
        var (code, stdout, _) = await RunAsync("run", Example, "--pin");

        Assert.Equal(CliApp.ExecutionFailed, code);
        Assert.Contains("▶ 1С: створити замовлення [mcp.tool] — Error", stdout, StringComparison.Ordinal);
        Assert.Contains("srv_1c", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualTriggerWorkflow_RunsWithoutServers()
    {
        var path = Path.Combine(_dir, "set.json");
        await File.WriteAllTextAsync(path, """
            {
              "formatVersion": 1, "name": "Set",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 30, "errorWorkflowId": null, "saveSuccessfulData": true },
              "nodes": [
                { "id": "t", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "s", "name": "Поле", "type": "core.set", "typeVersion": 1, "position": [200, 0],
                  "parameters": { "fields": [ { "name": "sum", "value": "{{ 2 + 3 }}" } ] } }
              ],
              "connections": [ { "from": { "node": "t", "output": 0 }, "to": { "node": "s", "input": 0 } } ],
              "pinData": {}
            }
            """);

        var (code, stdout, stderr) = await RunAsync("run", path);

        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("▶ Поле [core.set] — Success", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("build")]
    [InlineData("run")]
    [InlineData("run", "a.json", "b.json")]
    [InlineData("run", "a.json", "--verbose")]
    [InlineData("run", "a.json", "--servers")]
    [InlineData("run", "a.json", "--credentials")]
    public async Task BadArguments_ReturnUsageError(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(args);

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains("Використання:", stderr, StringComparison.Ordinal);
        Assert.Contains("openbp run <file.json>", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingWorkflowFile_ReturnsUsageError()
    {
        var (code, _, stderr) = await RunAsync("run", Path.Combine(_dir, "none.json"));

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains("Не вдалося прочитати сценарій", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownNodeType_ReturnsUsageError()
    {
        var workflow = WorkflowJson.Deserialize(await File.ReadAllTextAsync(Example));
        workflow.Nodes[2].Type = "telegram.send";
        var path = Path.Combine(_dir, "unknown.json");
        await File.WriteAllTextAsync(path, WorkflowJson.Serialize(workflow));

        var (code, _, stderr) = await RunAsync("run", path, "--pin");

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains("Сценарій не можна виконати", stderr, StringComparison.Ordinal);
        Assert.Contains("telegram.send", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "servers": [ { "id": "a", "transport": "stdio" } ] }""", "без command")]
    [InlineData("""{ "servers": [ { "id": "a", "transport": "http", "url": "not a url" } ] }""", "без коректного url")]
    [InlineData("""{ "servers": [ { "id": "a", "transport": "http", "url": "http://x/mcp" }, { "id": "a", "transport": "http", "url": "http://y/mcp" } ] }""", "двічі")]
    [InlineData("""{ "servers": [ { "id": "a", "transport": "pigeon" } ] }""", "Не вдалося прочитати файл серверів")]
    [InlineData("""{ "servers": [ { "transport": "stdio", "command": "x" } ] }""", "немає id")]
    public async Task InvalidServersFile_ReturnsUsageError(string json, string message)
    {
        var path = Path.Combine(_dir, "servers.json");
        await File.WriteAllTextAsync(path, json);

        var (code, _, stderr) = await RunAsync("run", Example, "--pin", "--servers", path);

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ServersFile_RelativeCwd_ResolvedFromFileDirectory()
    {
        var path = Path.Combine(_dir, "servers.json");
        File.WriteAllText(path, """
            { "servers": [
              { "id": "a", "transport": "stdio", "command": "x", "cwd": "sub" },
              { "id": "b", "name": "Б", "transport": "stdio", "command": "x", "args": ["--mock", "1c"] } ] }
            """);

        var servers = ServersFile.Load(path);

        Assert.Equal(Path.Combine(_dir, "sub"), servers[0].WorkingDirectory);
        Assert.Equal("a", servers[0].Name);
        Assert.Equal(_dir, servers[1].WorkingDirectory);
        Assert.Equal(["--mock", "1c"], servers[1].Arguments);
    }

    [Fact]
    public async Task OllamaExample_RunsFromWebhookPinDataThroughLlmToTelegramReply()
    {
        using var llm = new ChatCompletionsStub("""{"customer":"Петренко","lines":[{"sku":"ДОБРИВО-50","qty":2}]}""");
        var credentials = Path.Combine(_dir, "creds.json");
        await File.WriteAllTextAsync(credentials, JsonSerializer.Serialize(new { cred_ollama = new { baseUrl = llm.BaseUrl } }));

        var (code, stdout, stderr) = await RunAsync(
            "run", OllamaExample, "--pin", "--servers", WriteServers(), "--credentials", credentials);

        Assert.True(code == CliApp.Ok, stderr + stdout);
        Assert.Contains("▶ Розібрати заявку [ai.llm] — Success", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Розібрати заявку [ai.llm] — Success · pinData", stdout, StringComparison.Ordinal);
        Assert.Contains("\"number\": 1001", stdout, StringComparison.Ordinal);
        Assert.Contains("\"text\": \"Замовлення №1001 створено\"", stdout, StringComparison.Ordinal);
        Assert.Contains("Результат: Success", stdout, StringComparison.Ordinal);
        var request = Assert.Single(llm.Requests);
        using var body = JsonDocument.Parse(request);
        Assert.Contains("2 мішки добрива Петренку", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("json_schema", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task OllamaExample_WithoutCredentials_LlmNodeFails()
    {
        var (code, stdout, _) = await RunAsync("run", OllamaExample, "--pin", "--servers", WriteServers());

        Assert.Equal(CliApp.ExecutionFailed, code);
        Assert.Contains("▶ Розібрати заявку [ai.llm] — Error", stdout, StringComparison.Ordinal);
        Assert.Contains("cred_ollama", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "очікується JSON-об'єкт")]
    [InlineData("""{ "a": 1 }""", "має бути JSON-об'єктом")]
    [InlineData("не json", "Не вдалося прочитати файл credentials")]
    public async Task InvalidCredentialsFile_ReturnsUsageError(string json, string message)
    {
        var path = Path.Combine(_dir, "creds.json");
        await File.WriteAllTextAsync(path, json);

        var (code, _, stderr) = await RunAsync("run", Example, "--pin", "--credentials", path);

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExampleCredentialsFile_HasNoSecrets()
    {
        var provider = CredentialsFile.Load(Path.Combine(RepoRoot, "examples", "credentials.ollama.json"));

        var data = await provider.GetAsync("cred_ollama", CancellationToken.None);
        Assert.Equal("http://localhost:11434/v1", data["baseUrl"]!.GetValue<string>());
        Assert.Null(data["apiKey"]);
    }

    [Fact]
    public void PinOnlyStubs_RegisteredOnlyForMissingTypes()
    {
        var real = new StubNode(new NodeDescriptor("ai.llm", 1, "LLM", NodeGroup.Ai, ["main"], ["main"], []));
        var registry = new InMemoryNodeRegistry().Register(real);

        PinOnlyNode.RegisterMissing(registry, n => registry.Register(n));

        Assert.Same(real, registry.Resolve("ai.llm", 1));
        Assert.IsType<PinOnlyNode>(registry.Resolve("core.webhook", 1));
    }

    private static string Example => Path.Combine(RepoRoot, "examples", "telegram-1c.json");

    private static string DailyReport => Path.Combine(RepoRoot, "examples", "daily-report.json");

    private static string OrderTotal => Path.Combine(RepoRoot, "examples", "order-total.json");

    private static string YesterdayIn(TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string OllamaExample => Path.Combine(RepoRoot, "examples", "telegram-1c-ollama.json");

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Не знайдено корінь репозиторію (OpenBP.sln).");
        }
    }

    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    /// <summary>Файл серверів із моками 1С, Telegram і Graph, що стартують із dll FakeServer поруч із тестами.</summary>
    private string WriteServers()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");
        object Server(string id, string mock) => new { id, transport = "stdio", command = DotNet, args = new[] { dll, "--mock", mock } };
        var path = Path.Combine(_dir, "servers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { servers = new[] { Server("srv_1c", "1c"), Server("srv_telegram", "telegram"), Server("srv_msgraph", "msgraph") } }));
        return path;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, stdout, stderr, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private sealed class StubNode(NodeDescriptor descriptor) : INode
    {
        public NodeDescriptor Descriptor { get; } = descriptor;

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>In-process HTTP-стаб <c>POST /v1/chat/completions</c> у форматі OpenAI: віддає задану відповідь моделі.</summary>
    private sealed class ChatCompletionsStub : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public ChatCompletionsStub(string content)
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var context = await _listener.GetContextAsync();
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        var ok = context.Request.Url!.AbsolutePath == "/v1/chat/completions" && context.Request.HttpMethod == "POST";
                        if (ok)
                        {
                            Requests.Add(body);
                        }

                        var payload = Encoding.UTF8.GetBytes(ok
                            ? JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } })
                            : "{}");
                        context.Response.StatusCode = ok ? 200 : 404;
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(payload);
                        context.Response.Close();
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    // слухача зупинено
                }
            });
        }

        public string BaseUrl { get; }

        public ConcurrentBag<string> Requests { get; } = [];

        public void Dispose()
        {
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
