using System.Text.Json;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli.Tests;

/// <summary>
/// <c>openbp run</c> на прикладі <c>examples/telegram-1c.json</c> з моками 1С і Telegram на FakeServer.
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
    public async Task Example_WithoutPin_StubNodeFailsWithHint()
    {
        var (code, stdout, _) = await RunAsync("run", Example, "--servers", WriteServers());

        Assert.Equal(CliApp.ExecutionFailed, code);
        Assert.Contains("▶ Нова заявка [core.webhook] — Error", stdout, StringComparison.Ordinal);
        Assert.Contains("ще не реалізований (F2-D)", stdout, StringComparison.Ordinal);
        Assert.Contains("--pin", stdout, StringComparison.Ordinal);
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
    public async Task BadArguments_ReturnUsageError(params string[] args)
    {
        var (code, _, stderr) = await RunAsync(args);

        Assert.Equal(CliApp.UsageError, code);
        Assert.Contains("Використання: openbp run", stderr, StringComparison.Ordinal);
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
    public void PinOnlyStubs_RegisteredOnlyForMissingTypes()
    {
        var real = new StubNode(new NodeDescriptor("ai.llm", 1, "LLM", NodeGroup.Ai, ["main"], ["main"], []));
        var registry = new InMemoryNodeRegistry().Register(real);

        PinOnlyNode.RegisterMissing(registry, n => registry.Register(n));

        Assert.Same(real, registry.Resolve("ai.llm", 1));
        Assert.IsType<PinOnlyNode>(registry.Resolve("core.webhook", 1));
    }

    private static string Example => Path.Combine(RepoRoot, "examples", "telegram-1c.json");

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

    /// <summary>Файл серверів із моками 1С і Telegram, що стартують із dll FakeServer поруч із тестами.</summary>
    private string WriteServers()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");
        object Server(string id, string mock) => new { id, transport = "stdio", command = DotNet, args = new[] { dll, "--mock", mock } };
        var path = Path.Combine(_dir, "servers.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { servers = new[] { Server("srv_1c", "1c"), Server("srv_telegram", "telegram") } }));
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
}
