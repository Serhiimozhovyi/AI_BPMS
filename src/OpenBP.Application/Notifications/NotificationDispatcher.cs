using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenBP.Application.Auth;
using OpenBP.Application.Connectors;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Notifications;

/// <summary>502: канал сповіщень не прийняв повідомлення (тест каналу).</summary>
public sealed class NotificationDeliveryException : OpenBpException
{
    public NotificationDeliveryException(string message, Exception? inner = null)
        : base("NOTIFICATION_DELIVERY_FAILED", 502, message, inner)
    {
    }
}

/// <summary>
/// Розсилає outbox <c>notifications</c> (F6-07) у ввімкнені канали, підписані на подію. Cooldown — на канал і пару
/// «подія:предмет» (<c>notification_sends</c>): подія, що прийшла раніше ніж через <c>cooldownSec</c> після останньої
/// відправки, лише позначається обробленою. Збій каналу пишеться в журнал і не повторюється — сповіщення не повинні
/// накопичуватись. email і telegram ідуть через tool конектора від імені системи, webhook — POST JSON.
/// </summary>
public sealed partial class NotificationDispatcher(
    OpenBpDbContext db,
    IMcpServerRegistry registry,
    Func<ICredentialProvider> credentials,
    HttpClient http,
    ILogger<NotificationDispatcher> logger,
    TimeProvider time)
{
    /// <summary>Скільки подій обробляє один прохід.</summary>
    public const int BatchSize = 100;

    private const string ClaimSql = """
        UPDATE notifications SET processed_at = @now
        WHERE id IN (SELECT id FROM notifications WHERE processed_at IS NULL ORDER BY created_at LIMIT @limit FOR UPDATE SKIP LOCKED)
        RETURNING *
        """;

    /// <summary>Обробляє пачку нових подій; повертає кількість надісланих повідомлень.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<int> DispatchAsync(CancellationToken ct)
    {
        // Подію забирає рівно один прохід (кілька екземплярів Api чи job і тест каналу одночасно): позначка «оброблено»
        // ставиться до відправки, тож збій посеред пачки не дасть повторів — сповіщення доставляються не більше одного разу.
        var pending = await db.Notifications
            .FromSqlRaw(
                ClaimSql,
                new Npgsql.NpgsqlParameter("now", time.GetUtcNow()),
                new Npgsql.NpgsqlParameter("limit", BatchSize))
            .AsNoTracking()
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return 0;
        }

        var channels = (await db.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct))
            .Select(c => (Row: c, Events: JsonSerializer.Deserialize<List<string>>(c.Events) ?? []))
            .ToList();
        var sent = 0;
        foreach (var notification in pending.OrderBy(n => n.CreatedAt))
        {
            foreach (var (channel, _) in channels.Where(c => c.Events.Contains(notification.Event)))
            {
                var key = $"{notification.Event}:{notification.Subject}";
                var last = await db.NotificationSends.SingleOrDefaultAsync(s => s.ChannelId == channel.Id && s.Key == key, ct);
                if (last is not null && notification.CreatedAt < last.SentAt.AddSeconds(channel.CooldownSec))
                {
                    continue;
                }

                try
                {
                    await SendAsync(channel, notification, ct);
                }
                catch (NotificationDeliveryException ex)
                {
                    LogDeliveryFailed(logger, ex, channel.Name, notification.Event);
                    continue;
                }

                var now = time.GetUtcNow();
                if (last is null)
                {
                    db.NotificationSends.Add(new NotificationSendEntity { ChannelId = channel.Id, Key = key, SentAt = now });
                }
                else
                {
                    last.SentAt = now;
                }

                sent++;
                await db.SaveChangesAsync(ct);
            }
        }

        return sent;
    }

    /// <summary>Надсилає одне повідомлення в канал.</summary>
    /// <exception cref="NotificationDeliveryException">Канал не прийняв повідомлення.</exception>
    public async Task SendAsync(NotificationChannelEntity channel, NotificationEntity notification, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(notification);
        var config = JsonNode.Parse(channel.Config) as JsonObject ?? [];
        var details = JsonNode.Parse(notification.Details) as JsonObject ?? [];
        try
        {
            switch (channel.Type)
            {
                case NotificationChannelTypes.Email:
                    await CallToolAsync(config, "send_mail", new JsonObject
                    {
                        ["from"] = config["from"]?.DeepClone(),
                        ["to"] = config["to"]?.DeepClone(),
                        ["subject"] = $"OpenBP: {notification.Title}",
                        ["html"] = Html(notification, details),
                    }, ct);
                    break;

                case NotificationChannelTypes.Telegram:
                    await CallToolAsync(config, "send_message", new JsonObject
                    {
                        ["chat_id"] = ChatId(config["chatId"]?.GetValue<string>()),
                        ["text"] = Text(notification, details),
                    }, ct);
                    break;

                case NotificationChannelTypes.Webhook:
                    await PostAsync(config, notification, details, ct);
                    break;

                default:
                    throw new NotificationDeliveryException($"Невідомий тип каналу «{channel.Type}».");
            }
        }
        catch (Exception ex) when (ex is OpenBpException and not NotificationDeliveryException or ArgumentException or HttpRequestException or TaskCanceledException
            or CredentialNotFoundException or CredentialDecryptionException)
        {
            throw new NotificationDeliveryException($"Канал «{channel.Name}»: {ex.Message}", ex);
        }
    }

    private async Task CallToolAsync(JsonObject config, string tool, JsonObject arguments, CancellationToken ct)
    {
        var connectorId = Guid.Parse(config["connectorId"]!.GetValue<string>());
        var connectors = new ConnectorService(db, SystemUser.Instance, registry, credentials, time);
        var result = await connectors.CallToolAsync(connectorId, tool, arguments, ct);
        if (result.IsError)
        {
            var text = string.Join(" ", result.Content.Select(b => b["text"]?.GetValue<string>()).Where(t => !string.IsNullOrEmpty(t)));
            throw new NotificationDeliveryException($"tool {tool} повернув помилку{(text.Length == 0 ? "." : $": {text}")}");
        }
    }

    private async Task PostAsync(JsonObject config, NotificationEntity notification, JsonObject details, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config["url"]!.GetValue<string>())
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["event"] = notification.Event,
                ["subject"] = notification.Subject,
                ["title"] = notification.Title,
                ["at"] = notification.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                ["details"] = details.DeepClone(),
            }),
        };
        if (config["credentialId"]?.GetValue<string>() is { } credentialId
            && await credentials().GetAsync(credentialId, ct) is { } credential
            && credential["headers"] is JsonObject headers)
        {
            foreach (var (name, value) in headers)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text))
                {
                    request.Headers.TryAddWithoutValidation(name, text);
                }
            }
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationDeliveryException($"webhook відповів HTTP {(int)response.StatusCode}.");
        }
    }

    /// <summary>Id чату Telegram: число (особистий чат, група) або рядок (<c>@channel</c>).</summary>
    private static JsonNode? ChatId(string? chatId) =>
        long.TryParse(chatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? JsonValue.Create(id) : JsonValue.Create(chatId);

    /// <summary>Текст повідомлення: заголовок і подробиці «ключ: значення».</summary>
    internal static string Text(NotificationEntity notification, JsonObject details)
    {
        var text = new StringBuilder("OpenBP: ").Append(notification.Title);
        foreach (var (key, value) in details)
        {
            text.Append('\n').Append(key).Append(": ").Append(value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString());
        }

        return text.ToString();
    }

    private static string Html(NotificationEntity notification, JsonObject details)
    {
        static string Encode(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var html = new StringBuilder("<p><b>").Append(Encode(notification.Title)).Append("</b></p><ul>");
        foreach (var (key, value) in details)
        {
            html.Append("<li>").Append(Encode(key)).Append(": ")
                .Append(Encode(value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString())).Append("</li>");
        }

        return html.Append("</ul>").ToString();
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Сповіщення {Event} не надіслано в канал «{Channel}».")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception error, string channel, string @event);
}

/// <summary>Користувач «система» для дій без запиту (розсилка сповіщень): роль admin, без id.</summary>
internal sealed class SystemUser : ICurrentUser
{
    public static readonly SystemUser Instance = new();

    public bool IsAuthenticated => true;

    public Guid? UserId => null;

    public string? Email => null;

    public UserRole? Role => UserRole.Admin;
}
