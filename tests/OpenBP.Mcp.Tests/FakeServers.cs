using Microsoft.AspNetCore.Builder;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.FakeServer;

namespace OpenBP.Mcp.Tests;

/// <summary>Запуск FakeServer у тестах.</summary>
internal static class FakeServers
{
    /// <summary>stdio: <c>dotnet OpenBP.Mcp.FakeServer.dll</c> з теки тестів.</summary>
    public static McpServerConfig Stdio(string id = "srv_fake", params string[] args) => new()
    {
        Id = id,
        Name = "Fake (stdio)",
        Transport = McpTransportKind.Stdio,
        Command = DotNet,
        Arguments = [Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll"), .. args],
    };

    /// <summary>http: сервер у процесі тестів на вільному порту.</summary>
    public static async Task<HttpFakeServer> StartHttpAsync(string id = "srv_http")
    {
        var app = FakeServerApp.BuildHttp("http://127.0.0.1:0");
        await app.StartAsync();
        var url = new Uri(new Uri(app.Urls.First()), "/mcp");
        return new HttpFakeServer(app, new McpServerConfig
        {
            Id = id,
            Name = "Fake (http)",
            Transport = McpTransportKind.Http,
            Url = url,
        });
    }

    /// <summary>Шлях до <c>dotnet</c>, яким запущено тести (MSBuild/VSTest виставляють DOTNET_HOST_PATH).</summary>
    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
}

/// <summary>Запущений HTTP FakeServer.</summary>
internal sealed class HttpFakeServer(WebApplication app, McpServerConfig config) : IAsyncDisposable
{
    public McpServerConfig Config { get; } = config;

    /// <summary>Застосунок сервера (сервіси, напр. <see cref="FakeEvents"/>).</summary>
    public WebApplication App => app;

    public async Task StopAsync() => await app.StopAsync();

    public async ValueTask DisposeAsync()
    {
        try
        {
            await app.StopAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        await app.DisposeAsync();
    }
}
