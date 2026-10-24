using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// <c>--mock 1c</c> / <c>--mock telegram</c> / <c>--mock msgraph</c> — stdio-моки 1С, Telegram і Microsoft Graph для прикладів
/// <c>examples/telegram-1c.json</c> і <c>examples/daily-report.json</c>.
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
                "msgraph" => [typeof(MockGraphTools)],
                _ => [],
            };
        if (tools.Length == 0)
        {
            await Console.Error.WriteLineAsync("--mock: очікується 1c, telegram або msgraph.");
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

/// <summary>Мок 1С для прикладів <c>examples/telegram-1c.json</c> і <c>examples/daily-report.json</c> (<c>--mock 1c</c>).</summary>
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

    /// <summary>
    /// Продажі за період у форматі справжнього конектора (<c>connectors/onec</c>): три документи двох контрагентів,
    /// датовані першим днем періоду.
    /// </summary>
    [McpServerTool(Name = "sales_report", Title = "Звіт про продажі", UseStructuredContent = true)]
    [Description("Проведені документи продажу за період.")]
    public static SalesReport SalesReport(
        [Description("Перший день, YYYY-MM-DD")] string from,
        [Description("Останній день, YYYY-MM-DD")] string to)
    {
        var date = $"{from}T10:00:00";
        return new(
        [
            new("РН-0001", date, "ТОВ Агро", "6f1c2a8e-0d5b-4c33-9a5e-2b7d8f1e4a10", 1000.5m),
            new("РН-0002", date, "ФГ Лан", "0b9a7c11-52de-4e0f-8d3a-77c1e2f40a22", 250m),
            new("РН-0003", date, "ТОВ Агро", "6f1c2a8e-0d5b-4c33-9a5e-2b7d8f1e4a10", 99m),
        ]);
    }
}

/// <summary>Результат <c>sales_report</c>: <c>{ "result": [...] }</c>.</summary>
public sealed record SalesReport([property: JsonPropertyName("result")] IReadOnlyList<Sale> Result);

/// <summary>Документ продажу (snake_case, як у справжнього конектора).</summary>
public sealed record Sale(
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("customer_key")] string CustomerKey,
    [property: JsonPropertyName("amount")] decimal Amount);

/// <summary>Рядок замовлення 1С.</summary>
public sealed record OrderLine(string Sku, double Qty);

/// <summary>Результат <c>create_order</c>.</summary>
public sealed record CreatedOrder(int Number, string Customer, int LineCount);

/// <summary>
/// Мок Telegram Bot API (<c>--mock telegram</c>) для прикладів <c>examples/telegram-1c.json</c> і
/// <c>examples/psy-support-bot.json</c>: tools і аргументи — як у <c>connectors/telegram</c>, відповідь повторює запит.
/// </summary>
[McpServerToolType]
public static class MockTelegramTools
{
    /// <summary>«Надсилає» повідомлення в чат і повертає його як надіслане.</summary>
    [McpServerTool(Name = "send_message", Title = "Надіслати повідомлення", UseStructuredContent = true)]
    [Description("Надсилає текстове повідомлення в чат Telegram.")]
    public static SentMessage SendMessage(
        [Description("Id чату")] long chat_id,
        [Description("Текст")] string text,
        [Description("HTML, MarkdownV2 або Markdown")] string? parse_mode = null,
        [Description("Клавіатура (об'єкт Bot API)")] JsonElement? reply_markup = null) =>
        new(true, chat_id, text, parse_mode, Markup(reply_markup));

    /// <summary>«Редагує» повідомлення бота.</summary>
    [McpServerTool(Name = "edit_message_text", Title = "Змінити повідомлення", UseStructuredContent = true)]
    [Description("Замінює текст і inline-клавіатуру повідомлення бота.")]
    public static EditedMessage EditMessageText(
        [Description("Id чату")] long chat_id,
        [Description("Id повідомлення")] long message_id,
        [Description("Новий текст")] string text,
        [Description("HTML, MarkdownV2 або Markdown")] string? parse_mode = null,
        [Description("Нова inline-клавіатура")] JsonElement? reply_markup = null) =>
        new(true, chat_id, message_id, text, parse_mode, Markup(reply_markup));

    /// <summary>Підтверджує натискання кнопки.</summary>
    [McpServerTool(Name = "answer_callback_query", Title = "Відповісти на натискання", UseStructuredContent = true)]
    [Description("Підтверджує натискання inline-кнопки.")]
    public static AnsweredCallback AnswerCallbackQuery(
        [Description("callback_query.id")] string callback_query_id,
        [Description("Сповіщення")] string? text = null) => new(true, callback_query_id);

    /// <summary>Задає меню команд.</summary>
    [McpServerTool(Name = "set_my_commands", Title = "Команди бота", UseStructuredContent = true)]
    [Description("Задає меню команд бота.")]
    public static CommandsSet SetMyCommands(
        [Description("Команди: [{ command, description }]")] JsonElement commands,
        [Description("BotCommandScope")] JsonElement? scope = null) => new(true, commands, scope);

    private static JsonElement? Markup(JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Object } markup)
        {
            return null;
        }

        string[] kinds = ["inline_keyboard", "keyboard", "remove_keyboard", "force_reply"];
        return kinds.Count(k => markup.TryGetProperty(k, out _)) == 1
            ? markup
            : throw new McpException("reply_markup — об'єкт з одним з полів inline_keyboard, keyboard, remove_keyboard або force_reply.");
    }
}

/// <summary>Результат <c>send_message</c>.</summary>
public sealed record SentMessage(
    bool Ok,
    long ChatId,
    string Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ParseMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? ReplyMarkup = null);

/// <summary>Результат <c>edit_message_text</c>.</summary>
public sealed record EditedMessage(bool Ok, long ChatId, long MessageId, string Text, string? ParseMode, JsonElement? ReplyMarkup);

/// <summary>Результат <c>answer_callback_query</c>.</summary>
public sealed record AnsweredCallback(bool Ok, string CallbackQueryId);

/// <summary>Результат <c>set_my_commands</c>.</summary>
public sealed record CommandsSet(bool Ok, JsonElement Commands, JsonElement? Scope);

/// <summary>
/// Мок Microsoft Graph (<c>--mock msgraph</c>) для прикладу <c>examples/daily-report.json</c> і наскрізного тесту PRD №3:
/// дві вхідні листи (одна — «Рахунок» з PDF-вкладенням) і надсилання листа. Контракти — як у <c>connectors/msgraph</c>.
/// </summary>
[McpServerToolType]
public static class MockGraphTools
{
    /// <summary>Id листа з рахунком.</summary>
    public const string InvoiceMessageId = "AAMk-invoice-17";

    /// <summary>Id вкладення листа з рахунком.</summary>
    public const string InvoiceAttachmentId = "att-1";

    /// <summary>Назва файлу вкладення.</summary>
    public const string InvoiceFileName = "rakhunok-17.pdf";

    /// <summary>Вміст «PDF» вкладення.</summary>
    public static readonly byte[] InvoiceBytes = "%PDF-1.4 рахунок №17"u8.ToArray();

    private static readonly MailMessage[] Inbox =
    [
        new(InvoiceMessageId, "Рахунок №17 від ТОВ Агро", new MailFrom(new MailAddress("ТОВ Агро", "agro@example.com")), "2026-10-23T08:15:00Z", true),
        new("AAMk-hello-1", "Привітання", new MailFrom(new MailAddress("Олена", "olena@example.com")), "2026-10-23T09:00:00Z", false),
    ];

    /// <summary>Листи скриньки; <c>contains(subject,'…')</c> у <c>filter</c> відбирає за темою.</summary>
    [McpServerTool(Name = "list_messages", Title = "Листи", UseStructuredContent = true)]
    [Description("Повертає листи скриньки як { result: [...] }.")]
    public static MailMessages ListMessages(
        [Description("Скринька")] string mailbox,
        [Description("Тека")] string? folder = null,
        [Description("OData $filter")] string? filter = null)
    {
        var subject = filter is null ? null : System.Text.RegularExpressions.Regex.Match(filter, "contains\\(subject,\\s*'([^']*)'\\)") is { Success: true } m ? m.Groups[1].Value : null;
        return new([.. Inbox.Where(message => subject is null || message.Subject.Contains(subject, StringComparison.OrdinalIgnoreCase))]);
    }

    /// <summary>Вкладення листа без вмісту.</summary>
    [McpServerTool(Name = "list_attachments", Title = "Вкладення", UseStructuredContent = true)]
    [Description("Повертає вкладення листа як { result: [...] }.")]
    public static MailAttachments ListAttachments(
        [Description("Скринька")] string mailbox,
        [Description("Id листа")] string message_id) =>
        new(message_id == InvoiceMessageId
            ? [new MailAttachmentInfo(InvoiceAttachmentId, InvoiceFileName, "application/pdf", InvoiceBytes.Length, false)]
            : []);

    /// <summary>Вміст вкладення як <c>resource</c>-блок (у OpenBP — binary item).</summary>
    [McpServerTool(Name = "get_attachment", Title = "Завантажити вкладення")]
    [Description("Повертає вміст вкладення як embedded resource.")]
    public static CallToolResult GetAttachment(
        [Description("Скринька")] string mailbox,
        [Description("Id листа")] string message_id,
        [Description("Id вкладення")] string attachment_id)
    {
        if (message_id != InvoiceMessageId || attachment_id != InvoiceAttachmentId)
        {
            throw new McpException($"Вкладення {attachment_id} листа {message_id} не знайдено.");
        }

        return new CallToolResult
        {
            Content =
            [
                new EmbeddedResourceBlock
                {
                    Resource = new BlobResourceContents
                    {
                        Uri = $"msgraph://messages/{message_id}/attachments/{InvoiceFileName}",
                        MimeType = "application/pdf",
                        Blob = System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(InvoiceBytes)),
                    },
                },
            ],
        };
    }

    /// <summary>
    /// «Надсилає» лист. На відміну від справжнього конектора (<c>{ sent, from, to }</c>) повертає ще тему й тіло, щоб
    /// приклад і тести бачили, що саме пішло б адресату.
    /// </summary>
    [McpServerTool(Name = "send_mail", Title = "Надіслати лист", UseStructuredContent = true)]
    [Description("Надсилає HTML-лист зі скриньки.")]
    public static SentMail SendMail(
        [Description("Скринька відправника")] string from,
        [Description("Адреси отримувачів")] string[] to,
        [Description("Тема")] string subject,
        [Description("HTML-тіло")] string html) => new(true, from, to, subject, html);
}

/// <summary>Результат <c>send_mail</c>.</summary>
public sealed record SentMail(bool Sent, string From, string[] To, string Subject, string Html);

/// <summary>Результат <c>list_messages</c>.</summary>
public sealed record MailMessages([property: JsonPropertyName("result")] IReadOnlyList<MailMessage> Result);

/// <summary>Лист (поля — як у Graph).</summary>
public sealed record MailMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("from")] MailFrom From,
    [property: JsonPropertyName("receivedDateTime")] string ReceivedDateTime,
    [property: JsonPropertyName("hasAttachments")] bool HasAttachments);

/// <summary>Відправник листа.</summary>
public sealed record MailFrom([property: JsonPropertyName("emailAddress")] MailAddress EmailAddress);

/// <summary>Адреса.</summary>
public sealed record MailAddress([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("address")] string Address);

/// <summary>Результат <c>list_attachments</c>.</summary>
public sealed record MailAttachments([property: JsonPropertyName("result")] IReadOnlyList<MailAttachmentInfo> Result);

/// <summary>Вкладення без вмісту.</summary>
public sealed record MailAttachmentInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("isInline")] bool IsInline);
