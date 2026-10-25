using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Assistant;

/// <summary>Налаштування помічника (<c>GET/PUT /settings/assistant</c>).</summary>
/// <param name="CredentialId">Credential типу <c>anthropic</c>/<c>openai</c>/<c>openai-compatible</c>; <c>null</c> — не налаштовано.</param>
/// <param name="CredentialName">Назва credential'а (для показу).</param>
/// <param name="Provider">Тип credential'а — провайдер моделі.</param>
/// <param name="Model">Модель.</param>
public sealed record AssistantSettings(Guid? CredentialId, string? CredentialName, string? Provider, string? Model)
{
    /// <summary>Помічник налаштований: є credential і модель.</summary>
    public bool Configured => CredentialId is not null && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>Тіло <c>PUT /settings/assistant</c>.</summary>
/// <param name="CredentialId">Credential; <c>null</c> — вимкнути помічника.</param>
/// <param name="Model">Модель.</param>
public sealed record AssistantSettingsInput(Guid? CredentialId, string? Model);

/// <summary>Налаштування помічника в редакторі (F3-60): читає editor, змінює admin.</summary>
public interface IAssistantSettingsService
{
    /// <summary>Поточні налаштування.</summary>
    /// <param name="ct">Скасування.</param>
    Task<AssistantSettings> GetAsync(CancellationToken ct);

    /// <summary>Зберігає налаштування.</summary>
    /// <param name="input">Credential і модель.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Немає моделі або credential не того типу.</exception>
    /// <exception cref="NotFoundException">Credential'а немає.</exception>
    Task<AssistantSettings> SetAsync(AssistantSettingsInput input, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IAssistantSettingsService"/>: рядок <c>app_settings</c> з ключем <see cref="Key"/>.</summary>
public sealed class AssistantSettingsService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IAssistantSettingsService
{
    /// <summary>Ключ у <c>app_settings</c>.</summary>
    public const string Key = "assistant";

    private sealed record Stored(Guid? CredentialId, string? Model);

    /// <inheritdoc />
    public async Task<AssistantSettings> GetAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Editor);
        return await LoadAsync(db, ct);
    }

    /// <summary>Налаштування без перевірки прав — для системних операцій (пояснення сценарію у воркері, F3-72).</summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="ct">Скасування.</param>
    internal static async Task<AssistantSettings> LoadAsync(OpenBpDbContext db, CancellationToken ct)
    {
        var row = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == Key, ct);
        var stored = row is null ? null : JsonSerializer.Deserialize<Stored>(row.Value, JsonSerializerOptions.Web);
        if (stored?.CredentialId is not { } credentialId)
        {
            return new AssistantSettings(null, null, null, stored?.Model);
        }

        var credential = await db.Credentials.AsNoTracking().Where(c => c.Id == credentialId)
            .Select(c => new { c.Name, c.Type }).SingleOrDefaultAsync(ct);

        // Видалений credential — помічник не налаштований, модель лишається.
        return credential is null
            ? new AssistantSettings(null, null, null, stored.Model)
            : new AssistantSettings(credentialId, credential.Name, credential.Type, stored.Model);
    }

    /// <inheritdoc />
    public async Task<AssistantSettings> SetAsync(AssistantSettingsInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        currentUser.Require(UserRole.Admin);
        var model = input.Model?.Trim();
        if (input.CredentialId is { } credentialId)
        {
            var type = await db.Credentials.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.Type).SingleOrDefaultAsync(ct)
                ?? throw new NotFoundException("credential", credentialId);
            if (!LlmChat.Providers.Contains(type))
            {
                throw new ArgumentException(
                    $"Credential типу «{type}» не підходить для помічника. Оберіть credential типу {string.Join(", ", LlmChat.Providers)}.",
                    nameof(input));
            }

            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("Вкажіть модель помічника, наприклад claude-sonnet-5-5 або gpt-5.", nameof(input));
            }
        }

        var value = JsonSerializer.Serialize(new Stored(input.CredentialId, string.IsNullOrEmpty(model) ? null : model), JsonSerializerOptions.Web);
        var row = await db.AppSettings.SingleOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null)
        {
            db.AppSettings.Add(new AppSettingEntity { Key = Key, Value = value, UpdatedAt = time.GetUtcNow() });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        return await GetAsync(ct);
    }
}
