using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OpenBP.Connectors.Telegram.Tests;

/// <summary>Запит до <see cref="FakeBotApi"/>.</summary>
/// <param name="Method">Метод Bot API, напр. <c>sendMessage</c>.</param>
/// <param name="ContentType">Тип тіла.</param>
/// <param name="Json">JSON-тіло (для <c>application/json</c>).</param>
/// <param name="Form">Поля форми (для <c>multipart/form-data</c>); файл — як «назва:розмір:вміст».</param>
public sealed record BotRequest(string Method, string? ContentType, JsonElement? Json, IReadOnlyDictionary<string, string> Form);

/// <summary>
/// In-process фейк Telegram Bot API на випадковому порту: <c>/bot&lt;token&gt;/&lt;method&gt;</c>, з чужим токеном — 401
/// як у справжнього API; <see cref="FailWith"/> змушує відповіді бути помилкою (напр. 429 з <c>retry_after</c>).
/// </summary>
public sealed class FakeBotApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _token;

    private FakeBotApi(WebApplication app, string token)
    {
        _app = app;
        _token = token;
    }

    /// <summary>Запити зі справжнім токеном.</summary>
    public ConcurrentQueue<BotRequest> Requests { get; } = new();

    /// <summary>Якщо задано — кожна відповідь має цей статус і тіло.</summary>
    public (int Status, string Body)? FailWith { get; set; }

    /// <summary>Відповіді-помилки для окремих чатів (<c>chat_id</c> → статус і тіло).</summary>
    public ConcurrentDictionary<long, (int Status, string Body)> FailForChat { get; } = new();

    /// <summary>Базова адреса з кінцевим «/».</summary>
    public Uri BaseUrl => new(_app.Urls.Single() + "/");

    public static async Task<FakeBotApi> StartAsync(string token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeBotApi(app, token);
        app.MapPost("/{bot}/{method}", fake.HandleAsync);
        await app.StartAsync();
        return fake;
    }

    private async Task HandleAsync(HttpContext ctx, string bot, string method)
    {
        if (bot != $"bot{_token}")
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsync("""{"ok":false,"error_code":401,"description":"Unauthorized"}""");
            return;
        }

        JsonElement? json = null;
        var form = new Dictionary<string, string>();
        if (ctx.Request.HasFormContentType)
        {
            var data = await ctx.Request.ReadFormAsync();
            foreach (var (key, value) in data)
            {
                form[key] = value.ToString();
            }

            foreach (var file in data.Files)
            {
                using var reader = new StreamReader(file.OpenReadStream());
                form[file.Name] = $"{file.FileName}:{file.Length}:{await reader.ReadToEndAsync()}";
            }
        }
        else
        {
            json = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        }

        Requests.Enqueue(new BotRequest(method, ctx.Request.ContentType, json, form));
        if (FailWith is { } fail)
        {
            ctx.Response.StatusCode = fail.Status;
            await ctx.Response.WriteAsync(fail.Body);
            return;
        }

        if (json is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("chat_id", out var chatId) &&
            chatId.ValueKind == JsonValueKind.Number && FailForChat.TryGetValue(chatId.GetInt64(), out var chatFail))
        {
            ctx.Response.StatusCode = chatFail.Status;
            await ctx.Response.WriteAsync(chatFail.Body);
            return;
        }

        object? result = method switch
        {
            "sendMessage" => Message(json!.Value.GetProperty("chat_id"), text: json.Value.GetProperty("text").GetString()),
            "sendDocument" => Message(
                json?.GetProperty("chat_id") ?? JsonSerializer.SerializeToElement(long.Parse(form["chat_id"], System.Globalization.CultureInfo.InvariantCulture)),
                caption: json?.TryGetProperty("caption", out var c) == true ? c.GetString() : form.GetValueOrDefault("caption"),
                document: new { file_id = "BQACAgIAAxk", file_name = form.TryGetValue("document", out var f) ? f.Split(':')[0] : "remote.pdf" }),
            "getUpdates" => new object[]
            {
                new { update_id = 9001, message = new { message_id = 1, date = 1761200000, chat = new { id = 42, type = "private" }, text = "Привіт" } },
                new { update_id = 9002, callback_query = new { id = "cb1", data = "ok" } },
            },
            "getChat" => new { id = 42, type = "private", first_name = "Олена", username = "olena" },
            "editMessageText" => Message(json!.Value.GetProperty("chat_id"), text: json.Value.GetProperty("text").GetString()),
            "answerCallbackQuery" or "setMyCommands" or "setWebhook" or "deleteWebhook" => true,
            _ => null,
        };
        if (result is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("""{"ok":false,"error_code":404,"description":"Not Found: method not found"}""");
            return;
        }

        await ctx.Response.WriteAsJsonAsync(new { ok = true, result });
    }

    private static object Message(JsonElement chat, string? text = null, string? caption = null, object? document = null) => new
    {
        message_id = 77,
        date = 1761200000,
        chat = chat.ValueKind == JsonValueKind.Number
            ? (object)new { id = chat.GetInt64(), type = "private" }
            : new { id = -1001234567890, type = "channel", username = chat.GetString()!.TrimStart('@') },
        text,
        caption,
        document,
    };

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
