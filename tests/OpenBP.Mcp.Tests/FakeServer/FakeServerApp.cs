using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenBP.Mcp.FakeServer;

/// <summary>
/// Тестовий MCP-сервер. <c>dotnet OpenBP.Mcp.FakeServer.dll</c> — stdio;
/// <c>--http &lt;url&gt;</c> — Streamable HTTP на <c>&lt;url&gt;/mcp</c>. У тестах HTTP-варіант хоститься в процесі через <see cref="BuildHttp"/>.
/// </summary>
public static class FakeServerApp
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "openbp-fake";

    /// <summary>Точка входу.</summary>
    public static async Task<int> Main(string[] args)
    {
        var http = Array.IndexOf(args, "--http");
        if (http >= 0 && http + 1 < args.Length)
        {
            await using var app = BuildHttp(args[http + 1]);
            await app.RunAsync();
            return 0;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" })
            .WithStdioServerTransport()
            .WithTools([typeof(FakeTools)])
            .WithTools([typeof(CrashTools)]);
        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>HTTP-варіант (без <c>crash</c>, бо в тестах він живе в процесі тестів).</summary>
    /// <param name="url">Адреса прослуховування, напр. <c>http://127.0.0.1:0</c>.</param>
    public static WebApplication BuildHttp(string url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" })
            .WithHttpTransport()
            .WithTools([typeof(FakeTools)]);
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }
}

/// <summary>Tools тестового сервера.</summary>
[McpServerToolType]
public static class FakeTools
{
    /// <summary>Повертає текст без змін.</summary>
    [McpServerTool(Name = "echo", Title = "Відлуння"), Description("Повертає переданий текст.")]
    public static string Echo([Description("Текст")] string text) => text;

    /// <summary>Сума двох чисел як structuredContent.</summary>
    [McpServerTool(Name = "add", Title = "Сума", UseStructuredContent = true), Description("Додає два цілі числа.")]
    public static SumResult Add(int a, int b) => new(a + b);

    /// <summary>Чекає задану кількість мілісекунд.</summary>
    [McpServerTool(Name = "sleep"), Description("Чекає ms мілісекунд.")]
    public static async Task<string> Sleep(int ms, CancellationToken ct)
    {
        await Task.Delay(ms, ct);
        return "ok";
    }

    /// <summary>Завжди повертає результат з <c>isError: true</c>.</summary>
    [McpServerTool(Name = "fail"), Description("Повертає помилку з переданим текстом.")]
    public static string Fail(string reason) => throw new McpException(reason);

    /// <summary>По text-блоку на кожен id — «стрічка записів» для mcp.poll.</summary>
    [McpServerTool(Name = "feed"), Description("Повертає по text-блоку на кожен id.")]
    public static IEnumerable<ContentBlock> Feed(string[] ids) =>
        [.. ids.Select(id => new TextContentBlock { Text = id })];

    /// <summary>Один запис замовлення як structuredContent.</summary>
    [McpServerTool(Name = "order", UseStructuredContent = true), Description("Повертає замовлення з id.")]
    public static OrderResult Order(int id, string? customer = null) => new(new OrderInfo(id, customer ?? "ТОВ Агро"));

    /// <summary>Список замовлень як structuredContent (SDK загортає колекцію в <c>{ "result": [ … ] }</c>).</summary>
    [McpServerTool(Name = "orders", UseStructuredContent = true), Description("Повертає замовлення з переданими id.")]
    public static IReadOnlyList<OrderInfo> Orders(int[] ids) => [.. ids.Select(id => new OrderInfo(id, $"Клієнт {id}"))];

    /// <summary>Картинка 1×1 як image-блок.</summary>
    [McpServerTool(Name = "pixel"), Description("Повертає PNG-картинку.")]
    public static ImageContentBlock Pixel() => ImageContentBlock.FromBytes(PixelBytes, "image/png");

    /// <summary>Байти, які повертає <c>pixel</c>.</summary>
    public static readonly byte[] PixelBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
}

/// <summary>Результат <c>order</c>.</summary>
public sealed record OrderResult(OrderInfo Order);

/// <summary>Замовлення.</summary>
public sealed record OrderInfo(int Id, string Customer);

/// <summary>Результат <c>add</c>.</summary>
public sealed record SumResult(int Sum);

/// <summary>Tools, що валять процес (лише stdio).</summary>
[McpServerToolType]
public static class CrashTools
{
    /// <summary>Аварійно завершує процес сервера, не відповівши.</summary>
    [McpServerTool(Name = "crash"), Description("Завершує процес сервера.")]
    public static string Crash()
    {
        Environment.Exit(3);
        return "unreachable";
    }
}
