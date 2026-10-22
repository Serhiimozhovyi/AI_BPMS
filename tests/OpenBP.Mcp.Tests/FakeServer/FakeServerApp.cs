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
/// <c>--mock 1c</c> / <c>--mock telegram</c> — stdio-мок 1С або Telegram для прикладу <c>examples/telegram-1c.json</c>.
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

        var mock = Array.IndexOf(args, "--mock");
        Type[] tools = mock < 0
            ? [typeof(FakeTools), typeof(CrashTools)]
            : (mock + 1 < args.Length ? args[mock + 1] : null) switch
            {
                "1c" => [typeof(MockOneCTools)],
                "telegram" => [typeof(MockTelegramTools)],
                _ => [],
            };
        if (tools.Length == 0)
        {
            await Console.Error.WriteLineAsync("--mock: очікується 1c або telegram.");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var server = builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" })
            .WithStdioServerTransport();
        foreach (var type in tools)
        {
            server.WithTools([type]);
        }

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

/// <summary>Мок 1С для прикладу <c>examples/telegram-1c.json</c> (<c>--mock 1c</c>).</summary>
[McpServerToolType]
public static class MockOneCTools
{
    /// <summary>Номер першого замовлення, яке «створює» мок.</summary>
    public const int FirstOrderNumber = 1001;

    private static int _lastNumber = FirstOrderNumber - 1;

    /// <summary>Створює замовлення покупця і повертає його номер.</summary>
    [McpServerTool(Name = "create_order", Title = "Створити замовлення", UseStructuredContent = true)]
    [Description("Створює замовлення покупця в 1С.")]
    public static CreatedOrder CreateOrder(
        [Description("Покупець")] string customer,
        [Description("Рядки замовлення")] OrderLine[] lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return new(Interlocked.Increment(ref _lastNumber), customer, lines.Length);
    }
}

/// <summary>Рядок замовлення 1С.</summary>
public sealed record OrderLine(string Sku, double Qty);

/// <summary>Результат <c>create_order</c>.</summary>
public sealed record CreatedOrder(int Number, string Customer, int LineCount);

/// <summary>Мок Telegram Bot API для прикладу <c>examples/telegram-1c.json</c> (<c>--mock telegram</c>).</summary>
[McpServerToolType]
public static class MockTelegramTools
{
    /// <summary>«Надсилає» повідомлення в чат і повертає його як надіслане.</summary>
    [McpServerTool(Name = "send_message", Title = "Надіслати повідомлення", UseStructuredContent = true)]
    [Description("Надсилає текстове повідомлення в чат Telegram.")]
    public static SentMessage SendMessage(
        [Description("Id чату")] long chat_id,
        [Description("Текст")] string text) => new(true, chat_id, text);
}

/// <summary>Результат <c>send_message</c>.</summary>
public sealed record SentMessage(bool Ok, long ChatId, string Text);
