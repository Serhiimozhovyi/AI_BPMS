using System.Text.Json.Nodes;

namespace OpenBP.Application.Notifications;

/// <summary>Типи каналів сповіщень (F6-07).</summary>
public static class NotificationChannelTypes
{
    /// <summary>Лист через tool <c>send_mail</c> конектора msgraph: <c>{ connectorId, from, to: [] }</c>.</summary>
    public const string Email = "email";

    /// <summary>Повідомлення через tool <c>send_message</c> конектора telegram: <c>{ connectorId, chatId }</c>.</summary>
    public const string Telegram = "telegram";

    /// <summary>POST JSON на <c>{ url }</c>; заголовки — з поля <c>headers</c> credential'а <c>credentialId</c>.</summary>
    public const string Webhook = "webhook";

    /// <summary>Усі типи.</summary>
    public static readonly IReadOnlyList<string> All = [Email, Telegram, Webhook];
}

/// <summary>Канал сповіщень у відповідях API.</summary>
/// <param name="Id">Id.</param>
/// <param name="Name">Назва.</param>
/// <param name="Type">Тип (<see cref="NotificationChannelTypes"/>).</param>
/// <param name="Config">Налаштування типу (без секретів).</param>
/// <param name="Events">Події (<see cref="NotificationEvents"/>).</param>
/// <param name="CooldownSec">Мінімальний проміжок між повідомленнями про ту саму подію й предмет.</param>
/// <param name="Enabled">Чи активний.</param>
/// <param name="UpdatedAt">Час зміни.</param>
public sealed record NotificationChannel(
    Guid Id, string Name, string Type, JsonObject Config, IReadOnlyList<string> Events, int CooldownSec, bool Enabled, DateTimeOffset UpdatedAt);

/// <summary>Тіло створення чи зміни каналу.</summary>
public sealed class NotificationChannelInput
{
    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    public JsonObject Config { get; set; } = [];

    public List<string> Events { get; set; } = [];

    public int CooldownSec { get; set; } = NotificationChannelService.DefaultCooldownSec;

    public bool Enabled { get; set; } = true;
}

/// <summary>Канали сповіщень адміністратору (лише admin).</summary>
public interface INotificationChannelService
{
    /// <summary>Усі канали.</summary>
    Task<IReadOnlyList<NotificationChannel>> ListAsync(CancellationToken ct);

    /// <summary>Створює канал.</summary>
    /// <exception cref="ArgumentException">Некоректні налаштування.</exception>
    Task<NotificationChannel> CreateAsync(NotificationChannelInput input, CancellationToken ct);

    /// <summary>Змінює канал.</summary>
    Task<NotificationChannel> UpdateAsync(Guid id, NotificationChannelInput input, CancellationToken ct);

    /// <summary>Видаляє канал.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Надсилає тестове повідомлення в канал одразу, без cooldown.</summary>
    /// <exception cref="NotificationDeliveryException">Канал не прийняв повідомлення.</exception>
    Task TestAsync(Guid id, CancellationToken ct);
}
