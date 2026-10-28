using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenBP.Connectors.Telegram.Events;
using OpenBP.Connectors.Telegram.Tools;

namespace OpenBP.Connectors.Telegram;

/// <summary>Збирає MCP-сервер Telegram: налаштування, токен, <see cref="BotApiClient"/> і tools.</summary>
public static class ConnectorHost
{
    /// <summary>Назва сервера в <c>initialize</c>.</summary>
    public const string ServerName = "OpenBP.Connectors.Telegram";

    /// <summary>stdio-сервер: stdout — лише протокол MCP, логи — у stderr.</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    public static IHost BuildStdio(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        AddConnector(builder.Services, builder.Configuration);
        builder.Services.AddMcpServer(Configure).WithStdioServerTransport().WithTools<TelegramTools>();
        builder.Services.PostConfigure<McpServerOptions>(AnnotateInputs);
        return builder.Build();
    }

    /// <summary>Streamable HTTP на <paramref name="url"/> (ендпойнт <c>/mcp</c>).</summary>
    /// <param name="args">Аргументи командного рядка (також джерело конфігурації).</param>
    /// <param name="url">Адреса прослуховування, напр. <c>http://0.0.0.0:8080</c>.</param>
    /// <param name="environment">Джерело змінних середовища для токена (у тестах — словник).</param>
    public static WebApplication BuildHttp(string[] args, string url, Func<string, string?>? environment = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(url);
        // Файл у base64 до 50 МБ (TelegramTools.MaxUploadBytes) — це понад 66 МБ JSON; типові 30 МБ Kestrel обірвали б запит.
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 80L * 1024 * 1024);
        AddConnector(builder.Services, builder.Configuration, environment);
        // Сесії для клієнтів з initialize: лише в сесії сервер може сам надіслати notifications/resources/updated (EventHub).
        builder.Services.AddMcpServer(Configure)
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<TelegramTools>();
        builder.Services.PostConfigure<McpServerOptions>(AnnotateInputs);
        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    /// <summary>Налаштування, токен і типізований <see cref="BotApiClient"/>.</summary>
    /// <param name="services">Сервіси.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <param name="environment">Джерело змінних середовища; <c>null</c> — середовище процесу.</param>
    public static void AddConnector(IServiceCollection services, IConfiguration configuration, Func<string, string?>? environment = null)
    {
        services.AddOptions<ConnectorOptions>()
            .Bind(configuration.GetSection(ConnectorOptions.Section))
            .Validate(o => o.BaseUrl is { IsAbsoluteUri: true }, "Connector:BaseUrl — абсолютна адреса Bot API.")
            .ValidateOnStart();
        services.AddSingleton(new ConnectorSecrets(environment ?? Environment.GetEnvironmentVariable));

        // Токен — у шляху запиту: журнали HttpClient (URL на рівні Information) вимкнено, щоб він не потрапив у логи.
        services.ConfigureHttpClientDefaults(http => http.RemoveAllLoggers());
        services.AddHttpClient<BotApiClient>((sp, http) =>
        {
            var baseUrl = sp.GetRequiredService<IOptions<ConnectorOptions>>().Value.BaseUrl!;
            http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        });

        // Стрічка telegram://updates для mcp.subscribe (docs/connectors.md §9.2).
        services.AddSingleton(TelegramEvents.Create);
        EventHub.Register(services);
    }

    private static void Configure(McpServerOptions options) =>
        options.ServerInfo = new Implementation { Name = ServerName, Version = "1.0.0" };

    /// <summary>
    /// <c>chat_id</c> усіх tools — з анотацією <c>x-openbp-options</c> (docs/connectors.md §2): у формі OpenBP чат обирається
    /// з чатів останніх оновлень <c>get_updates</c>; ввести id чи вираз можна, як і раніше.
    /// </summary>
    /// <param name="options">Налаштування MCP-сервера з уже зареєстрованими tools.</param>
    internal static void AnnotateChatIds(McpServerOptions options)
    {
        foreach (var tool in options.ToolCollection ?? [])
        {
            var protocol = tool.ProtocolTool;
            if (protocol.Name == "get_updates" || JsonNode.Parse(protocol.InputSchema.GetRawText()) is not JsonObject schema
                || schema["properties"]?["chat_id"] is not JsonObject chatId)
            {
                continue;
            }

            chatId["x-openbp-widget"] = "chat";
            chatId["x-openbp-options"] = new JsonObject
            {
                ["tool"] = "get_updates",
                ["arguments"] = new JsonObject { ["limit"] = 100 },
                ["itemsPath"] = "result",
                ["valuePath"] = "message.chat.id",
                ["labelPath"] = new JsonArray("message.chat.title", "message.chat.username", "message.chat.first_name"),
            };
            protocol.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
    }

    private static readonly JsonArray ParseModes = new("HTML", "MarkdownV2", "Markdown");

    /// <summary>Види оновлень Bot API для <c>allowed_updates</c> (множинний вибір у формі).</summary>
    private static readonly string[] UpdateTypes =
    [
        "message", "edited_message", "channel_post", "edited_channel_post", "callback_query", "inline_query", "chosen_inline_result",
        "shipping_query", "pre_checkout_query", "poll", "poll_answer", "my_chat_member", "chat_member", "chat_join_request",
    ];

    /// <summary>
    /// Анотації й межі для повних форм у конструкторі (F6-30, docs/connectors.md §2): <c>reply_markup</c> — редактор кнопок
    /// (<c>x-openbp-widget: keyboard</c>), <c>commands</c> — таблиця, <c>chat_id</c> — вибір чату; <c>parse_mode</c> — список,
    /// <c>text</c>/<c>caption</c> — <c>maxLength</c> (лічильник символів), <c>allowed_updates</c> — множинний вибір.
    /// Тут лише метадані схеми: поведінка tools не змінюється.
    /// </summary>
    /// <param name="options">Налаштування MCP-сервера з уже зареєстрованими tools.</param>
    internal static void AnnotateInputs(McpServerOptions options)
    {
        AnnotateChatIds(options);
        foreach (var tool in options.ToolCollection ?? [])
        {
            var protocol = tool.ProtocolTool;
            if (JsonNode.Parse(protocol.InputSchema.GetRawText()) is not JsonObject schema || schema["properties"] is not JsonObject properties)
            {
                continue;
            }

            var name = protocol.Name;
            if (properties["reply_markup"] is JsonObject markup)
            {
                markup["x-openbp-widget"] = "keyboard";
            }

            if (properties["commands"] is JsonObject commands)
            {
                commands["x-openbp-widget"] = "commands";
            }

            if (properties["parse_mode"] is JsonObject parseMode)
            {
                parseMode["enum"] = ParseModes.DeepClone();
            }

            if (properties["text"] is JsonObject text)
            {
                text["maxLength"] = name == "answer_callback_query" ? 200 : 4096;
            }

            if (properties["caption"] is JsonObject caption)
            {
                caption["maxLength"] = 1024;
            }

            if (properties["allowed_updates"] is JsonObject allowed)
            {
                allowed["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. UpdateTypes.Select(u => (JsonNode)JsonValue.Create(u)!)]) };
            }

            protocol.InputSchema = JsonSerializer.SerializeToElement(schema);
        }
    }
}
