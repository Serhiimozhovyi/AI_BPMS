using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Notifications;

/// <summary>Реалізація <see cref="INotificationChannelService"/> над <c>notification_channels</c>.</summary>
public sealed class NotificationChannelService(
    OpenBpDbContext db, ICurrentUser currentUser, NotificationDispatcher dispatcher, TimeProvider time) : INotificationChannelService
{
    /// <summary>Cooldown за замовчуванням, с.</summary>
    public const int DefaultCooldownSec = 900;

    /// <summary>Найбільший cooldown, с (доба).</summary>
    public const int MaxCooldownSec = 86_400;

    private const string Entity = "notificationChannel";

    /// <inheritdoc />
    public async Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var rows = await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    /// <inheritdoc />
    public async Task<NotificationChannel> CreateAsync(NotificationChannelInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var now = time.GetUtcNow();
        var row = new NotificationChannelEntity { Id = Guid.CreateVersion7(now), CreatedAt = now };
        await ApplyAsync(row, input, ct);
        row.UpdatedAt = now;
        db.NotificationChannels.Add(row);
        db.Audit(currentUser, now, AuditActions.NotificationChannelCreated, AuditEntities.NotificationChannel, row.Id.ToString(), AuditDetails(row));
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    /// <inheritdoc />
    public async Task<NotificationChannel> UpdateAsync(Guid id, NotificationChannelInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var row = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await ApplyAsync(row, input, ct);
        row.UpdatedAt = time.GetUtcNow();
        db.Audit(currentUser, row.UpdatedAt, AuditActions.NotificationChannelUpdated, AuditEntities.NotificationChannel, id.ToString(), AuditDetails(row));
        await db.SaveChangesAsync(ct);
        return ToDto(row);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var row = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        db.NotificationChannels.Remove(row);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.NotificationChannelDeleted, AuditEntities.NotificationChannel, id.ToString(), AuditDetails(row));
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task TestAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var row = await db.NotificationChannels.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        var now = time.GetUtcNow();
        await dispatcher.SendAsync(row, new NotificationEntity
        {
            Id = Guid.CreateVersion7(now),
            CreatedAt = now,
            Event = "test",
            Subject = "test",
            Title = "Тестове сповіщення OpenBP",
            Details = new JsonObject { ["channel"] = row.Name }.ToJsonString(),
        }, ct);
    }

    /// <summary>Перевіряє тіло й переносить у рядок.</summary>
    private async Task ApplyAsync(NotificationChannelEntity row, NotificationChannelInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
        {
            throw new ArgumentException("Назва каналу обов'язкова, до 200 символів.", nameof(input));
        }

        if (!NotificationChannelTypes.All.Contains(input.Type))
        {
            throw new ArgumentException($"Тип каналу «{input.Type}»: очікується {string.Join(", ", NotificationChannelTypes.All)}.", nameof(input));
        }

        var unknown = (input.Events ?? []).Where(e => !NotificationEvents.All.Contains(e)).ToList();
        if (unknown.Count > 0 || input.Events is not { Count: > 0 })
        {
            throw new ArgumentException(
                $"Події каналу: хоча б одна з {string.Join(", ", NotificationEvents.All)}" + (unknown.Count > 0 ? $"; невідомі: {string.Join(", ", unknown)}." : "."),
                nameof(input));
        }

        if (input.CooldownSec is < 0 or > MaxCooldownSec)
        {
            throw new ArgumentException($"cooldownSec — від 0 до {MaxCooldownSec}.", nameof(input));
        }

        var config = await ValidateConfigAsync(input.Type, input.Config ?? [], ct);
        row.Name = name;
        row.Type = input.Type;
        row.Config = config.ToJsonString();
        row.Events = JsonSerializer.Serialize(input.Events.Distinct(StringComparer.Ordinal).ToList());
        row.CooldownSec = input.CooldownSec;
        row.Enabled = input.Enabled;
    }

    /// <summary>Лише відомі поля типу; посилання на конектор і credential мають існувати.</summary>
    private async Task<JsonObject> ValidateConfigAsync(string type, JsonObject config, CancellationToken ct)
    {
        string Text(string field, bool required = true)
        {
            var value = config[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
            return value.Length == 0 && required ? throw new ArgumentException($"Канал {type}: поле {field} обов'язкове.", nameof(config)) : value;
        }

        async Task<string> Connector()
        {
            var raw = Text("connectorId");
            if (!Guid.TryParse(raw, out var id) || !await db.McpServers.AnyAsync(s => s.Id == id, ct))
            {
                throw new ArgumentException($"Канал {type}: конектора «{raw}» немає.", nameof(config));
            }

            return id.ToString();
        }

        switch (type)
        {
            case NotificationChannelTypes.Email:
                var to = (config["to"] as JsonArray ?? [])
                    .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")
                    .Where(s => s.Length > 0)
                    .ToList();
                if (to.Count == 0 || to.Any(a => !a.Contains('@', StringComparison.Ordinal)))
                {
                    throw new ArgumentException("Канал email: to — хоча б одна адреса.", nameof(config));
                }

                return new JsonObject { ["connectorId"] = await Connector(), ["from"] = Text("from"), ["to"] = new JsonArray([.. to.Select(a => (JsonNode)a)]) };

            case NotificationChannelTypes.Telegram:
                return new JsonObject { ["connectorId"] = await Connector(), ["chatId"] = Text("chatId") };

            default:
                var url = Text("url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    throw new ArgumentException("Канал webhook: url — абсолютна http(s)-адреса.", nameof(config));
                }

                var result = new JsonObject { ["url"] = uri.AbsoluteUri };
                var credential = Text("credentialId", required: false);
                if (credential.Length > 0)
                {
                    if (!Guid.TryParse(credential, out var credentialId) || !await db.Credentials.AnyAsync(c => c.Id == credentialId, ct))
                    {
                        throw new ArgumentException($"Канал webhook: облікових даних «{credential}» немає.", nameof(config));
                    }

                    result["credentialId"] = credentialId.ToString();
                }

                return result;
        }
    }

    private static JsonObject AuditDetails(NotificationChannelEntity row) => new() { ["name"] = row.Name, ["type"] = row.Type };

    internal static NotificationChannel ToDto(NotificationChannelEntity row) => new(
        row.Id,
        row.Name,
        row.Type,
        JsonNode.Parse(row.Config) as JsonObject ?? [],
        JsonSerializer.Deserialize<List<string>>(row.Events) ?? [],
        row.CooldownSec,
        row.Enabled,
        row.UpdatedAt);
}
