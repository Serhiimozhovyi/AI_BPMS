using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using OpenBP.Mcp.Client;

namespace OpenBP.Mcp.Tests;

public sealed class McpServerRegistryTests
{
    /// <summary>Запас на планувальник і завершення процесу понад таймаут.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task StdioDiscoveryReturnsToolSchemas()
    {
        await using var registry = new McpServerRegistry();
        await registry.RegisterAsync(FakeServers.Stdio());

        var cache = await registry.RefreshToolsAsync("srv_fake");

        AssertFakeTools(cache);
        Assert.Contains(cache.Tools, t => t.Name == "crash");
        Assert.Same(cache, registry.GetToolsCache("srv_fake"));
        Assert.True(cache.CachedAt > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task HttpDiscoveryReturnsToolSchemas()
    {
        await using var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry();
        await registry.RegisterAsync(server.Config);

        var cache = await registry.RefreshToolsAsync("srv_http");

        AssertFakeTools(cache);
        Assert.DoesNotContain(cache.Tools, t => t.Name == "crash");
    }

    [Fact]
    public async Task SessionIsPooledBetweenCalls()
    {
        await using var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry();
        await registry.RegisterAsync(server.Config);

        var first = await registry.RunAsync("srv_http", (c, _) => Task.FromResult(c));
        var text = await registry.RunAsync("srv_http", (c, ct) => CallTextAsync(c, "echo", new() { ["text"] = "привіт" }, ct));
        var second = await registry.RunAsync("srv_http", (c, _) => Task.FromResult(c));

        Assert.Equal("привіт", text);
        Assert.Same(first, second);
        Assert.Equal(1, registry.OpenSessionCount);
    }

    [Fact]
    public async Task StdioCrashThrowsUnavailableWithinTimeoutAndPoolRecovers()
    {
        var timeout = TimeSpan.FromSeconds(10);
        await using var registry = new McpServerRegistry(new McpRegistryOptions { RequestTimeout = timeout });
        await registry.RegisterAsync(FakeServers.Stdio());
        await registry.RefreshToolsAsync("srv_fake");

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<McpUnavailableException>(() =>
            registry.RunAsync("srv_fake", (c, ct) => CallTextAsync(c, "crash", [], ct)));
        watch.Stop();

        Assert.Equal("srv_fake", ex.ServerId);
        Assert.True(watch.Elapsed <= timeout + Slack, $"Падіння виявлено за {watch.Elapsed}, таймаут {timeout}.");

        // Наступний виклик піднімає новий процес.
        var text = await registry.RunAsync("srv_fake", (c, ct) => CallTextAsync(c, "echo", new() { ["text"] = "знову" }, ct));
        Assert.Equal("знову", text);
    }

    [Fact]
    public async Task StoppedHttpServerThrowsUnavailableWithinTimeout()
    {
        var timeout = TimeSpan.FromSeconds(3);
        var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry(new McpRegistryOptions { RequestTimeout = timeout, ConnectTimeout = timeout });
        await registry.RegisterAsync(server.Config);
        await registry.RefreshToolsAsync("srv_http");
        await server.DisposeAsync();

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<McpUnavailableException>(() =>
            registry.RunAsync("srv_http", (c, ct) => CallTextAsync(c, "echo", new() { ["text"] = "x" }, ct)));
        watch.Stop();

        Assert.Equal("srv_http", ex.ServerId);
        Assert.True(watch.Elapsed <= timeout + Slack, $"Помилку отримано за {watch.Elapsed}, таймаут {timeout}.");
    }

    [Fact]
    public async Task SlowToolThrowsUnavailableAfterTimeout()
    {
        await using var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry();
        await registry.RegisterAsync(server.Config);
        await registry.RefreshToolsAsync("srv_http");
        var timeout = TimeSpan.FromMilliseconds(300);

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<McpUnavailableException>(() =>
            registry.RunAsync("srv_http", (c, ct) => CallTextAsync(c, "sleep", new() { ["ms"] = 10_000 }, ct), timeout));
        watch.Stop();

        Assert.Contains("не відповів", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed <= timeout + Slack, $"Таймаут спрацював за {watch.Elapsed}.");

        // Сесія жива — сервер не падав, лише повільний tool.
        Assert.Equal("ok", await registry.RunAsync("srv_http", (c, ct) => CallTextAsync(c, "sleep", new() { ["ms"] = 1 }, ct)));
    }

    [Fact]
    public async Task UnreachableHttpServerThrowsUnavailableWithinConnectTimeout()
    {
        var connect = TimeSpan.FromSeconds(3);
        await using var registry = new McpServerRegistry(new McpRegistryOptions { ConnectTimeout = connect });
        await registry.RegisterAsync(new McpServerConfig
        {
            Id = "srv_down",
            Name = "Down",
            Transport = McpTransportKind.Http,
            Url = new Uri($"http://127.0.0.1:{FreePort()}/mcp"),
        });

        var watch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<McpUnavailableException>(() => registry.RefreshToolsAsync("srv_down"));
        watch.Stop();

        Assert.Equal("srv_down", ex.ServerId);
        Assert.True(watch.Elapsed <= connect + Slack, $"Помилку отримано за {watch.Elapsed}.");
        Assert.Null(registry.GetToolsCache("srv_down"));
    }

    [Fact]
    public async Task MissingStdioCommandThrowsUnavailable()
    {
        await using var registry = new McpServerRegistry(new McpRegistryOptions { ConnectTimeout = TimeSpan.FromSeconds(3) });
        await registry.RegisterAsync(new McpServerConfig
        {
            Id = "srv_missing",
            Name = "Missing",
            Transport = McpTransportKind.Stdio,
            Command = Path.Combine(Path.GetTempPath(), $"openbp-no-such-{Guid.NewGuid():N}"),
        });

        await Assert.ThrowsAsync<McpUnavailableException>(() => registry.RefreshToolsAsync("srv_missing"));
    }

    [Fact]
    public async Task ProtocolErrorIsNotUnavailable()
    {
        await using var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry();
        await registry.RegisterAsync(server.Config);

        var ex = await Record.ExceptionAsync(() => registry.RunAsync(
            "srv_http",
            async (c, ct) => await c.CallToolAsync("no_such_tool", new Dictionary<string, object?>(), cancellationToken: ct)));

        Assert.IsAssignableFrom<McpException>(ex);
        Assert.Equal(1, registry.OpenSessionCount);
    }

    [Fact]
    public async Task CallerCancellationIsNotUnavailable()
    {
        await using var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry();
        await registry.RegisterAsync(server.Config);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.RunAsync("srv_http", (c, ct) => CallTextAsync(c, "sleep", new() { ["ms"] = 10_000 }, ct), ct: cts.Token));
    }

    [Fact]
    public async Task IdleSessionIsEvicted()
    {
        await using var server = await FakeServers.StartHttpAsync();
        await using var registry = new McpServerRegistry(new McpRegistryOptions { IdleTimeout = TimeSpan.FromMilliseconds(100) });
        await registry.RegisterAsync(server.Config);
        await registry.RefreshToolsAsync("srv_http");
        Assert.Equal(1, registry.OpenSessionCount);

        await Task.Delay(300);
        var closed = await registry.EvictIdleAsync();

        Assert.Equal(1, closed);
        Assert.Equal(0, registry.OpenSessionCount);
        Assert.Equal("так", await registry.RunAsync("srv_http", (c, ct) => CallTextAsync(c, "echo", new() { ["text"] = "так" }, ct)));
    }

    [Fact]
    public async Task UnknownServerThrows()
    {
        await using var registry = new McpServerRegistry();

        var ex = await Assert.ThrowsAsync<McpServerUnknownException>(() => registry.RefreshToolsAsync("srv_nope"));

        Assert.Equal("srv_nope", ex.ServerId);
        Assert.Throws<McpServerUnknownException>(() => registry.SetToolsCache("srv_nope", new ToolsCache([], DateTimeOffset.UtcNow)));
        Assert.Null(registry.GetToolsCache("srv_nope"));
    }

    [Fact]
    public async Task RegisterReplacesConfigAndResetsCache()
    {
        await using var registry = new McpServerRegistry();
        var config = FakeServers.Stdio();
        await registry.RegisterAsync(config);
        registry.SetToolsCache(config.Id, new ToolsCache([new McpToolInfo("echo", null, null, [])], DateTimeOffset.UtcNow));

        await registry.RegisterAsync(config with { Name = "Перейменований" });

        Assert.True(registry.TryGetServer(config.Id, out var current));
        Assert.Equal("Перейменований", current.Name);
        Assert.Null(registry.GetToolsCache(config.Id));
        Assert.Single(registry.Servers);
        Assert.True(await registry.UnregisterAsync(config.Id));
        Assert.False(await registry.UnregisterAsync(config.Id));
        Assert.Empty(registry.Servers);
    }

    [Fact]
    public async Task InvalidConfigIsRejected()
    {
        await using var registry = new McpServerRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(
            new McpServerConfig { Id = "a", Name = "a", Transport = McpTransportKind.Stdio }));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(
            new McpServerConfig { Id = "b", Name = "b", Transport = McpTransportKind.Http }));
    }

    private static void AssertFakeTools(ToolsCache cache)
    {
        var echo = cache.Find("echo");
        Assert.NotNull(echo);
        Assert.Equal("Відлуння", echo.Title);
        Assert.Equal("Повертає переданий текст.", echo.Description);
        Assert.Equal("string", echo.InputSchema["properties"]?["text"]?["type"]?.GetValue<string>());
        Assert.Contains("text", echo.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));

        var add = cache.Find("add");
        Assert.NotNull(add);
        Assert.Equal("integer", add.InputSchema["properties"]?["a"]?["type"]?.GetValue<string>());
        Assert.NotNull(add.OutputSchema);
        Assert.NotNull(add.OutputSchema["properties"]?["sum"]);

        Assert.NotNull(cache.Find("sleep"));
        Assert.Null(cache.Find("Echo"));
    }

    private static async Task<string> CallTextAsync(McpClient client, string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
        var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().First().Text;
        return result.IsError == true ? throw new InvalidOperationException(text) : text;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
