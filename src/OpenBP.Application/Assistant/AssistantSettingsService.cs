using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBP.Ai;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Assistant;

/// <summary>Підключена модель помічника (F3-107).</summary>
/// <param name="CredentialId">Credential типу <c>anthropic</c>/<c>openai</c>/<c>gemini</c>/<c>openai-compatible</c>/<c>ollama</c>.</param>
/// <param name="CredentialName">Назва credential'а (для показу); <c>null</c> — credential видалено.</param>
/// <param name="Provider">Тип credential'а — провайдер моделі.</param>
/// <param name="Model">Модель.</param>
public sealed record AssistantProfile(Guid CredentialId, string? CredentialName, string? Provider, string Model);

/// <summary>Налаштування помічника (<c>GET/PUT /settings/assistant</c>).</summary>
/// <param name="CredentialId">Credential основної моделі; <c>null</c> — не налаштовано.</param>
/// <param name="CredentialName">Назва credential'а (для показу).</param>
/// <param name="Provider">Тип credential'а — провайдер моделі.</param>
/// <param name="Model">Основна модель — її використовують побудова з опису, чат і пояснення сценаріїв.</param>
/// <param name="Profiles">Усі підключені моделі (F3-107); основна — <see cref="PrimaryIndex"/>-та.</param>
/// <param name="PrimaryIndex">Індекс основної моделі в <see cref="Profiles"/>; <c>null</c> — помічник вимкнено.</param>
public sealed record AssistantSettings(
    Guid? CredentialId,
    string? CredentialName,
    string? Provider,
    string? Model,
    IReadOnlyList<AssistantProfile>? Profiles = null,
    int? PrimaryIndex = null)
{
    /// <summary>Помічник налаштований: є credential і модель.</summary>
    public bool Configured => CredentialId is not null && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>Модель у тілі <c>PUT /settings/assistant</c>.</summary>
/// <param name="CredentialId">Credential.</param>
/// <param name="Model">Модель.</param>
public sealed record AssistantProfileInput(Guid CredentialId, string? Model);

/// <summary>Тіло <c>PUT /settings/assistant</c>.</summary>
/// <param name="CredentialId">Credential основної моделі; <c>null</c> — вимкнути помічника. Ігнорується, якщо задано <paramref name="Profiles"/>.</param>
/// <param name="Model">Основна модель. Ігнорується, якщо задано <paramref name="Profiles"/>.</param>
/// <param name="Profiles">Список підключених моделей (F3-107); без нього — одна модель із <paramref name="CredentialId"/>.</param>
/// <param name="PrimaryIndex">Індекс основної моделі в <paramref name="Profiles"/>; типово перша.</param>
public sealed record AssistantSettingsInput(Guid? CredentialId, string? Model, IReadOnlyList<AssistantProfileInput>? Profiles = null, int? PrimaryIndex = null);

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

    // CredentialId/Model — дзеркало основної моделі: старі читачі й розгорнуті БД працюють без змін (F3-107).
    private sealed record Stored(Guid? CredentialId, string? Model, List<StoredProfile>? Profiles = null, int? PrimaryIndex = null);

    private sealed record StoredProfile(Guid CredentialId, string Model);

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

        var profiles = stored.Profiles is { Count: > 0 } list
            ? list
            : [new StoredProfile(credentialId, stored.Model ?? "")];
        var ids = profiles.Select(x => x.CredentialId).Distinct().ToList();
        var known = await db.Credentials.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Type }).ToDictionaryAsync(c => c.Id, ct);
        var view = profiles.Select(x => known.TryGetValue(x.CredentialId, out var c)
            ? new AssistantProfile(x.CredentialId, c.Name, c.Type, x.Model)
            : new AssistantProfile(x.CredentialId, null, null, x.Model)).ToList();

        // Видалений credential основної моделі — помічник не налаштований, модель лишається.
        return known.TryGetValue(credentialId, out var primary)
            ? new AssistantSettings(credentialId, primary.Name, primary.Type, stored.Model, view, Math.Clamp(stored.PrimaryIndex ?? 0, 0, view.Count - 1))
            : new AssistantSettings(null, null, null, stored.Model, view, null);
    }

    /// <inheritdoc />
    public async Task<AssistantSettings> SetAsync(AssistantSettingsInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        currentUser.Require(UserRole.Admin);
        var model = input.Model?.Trim();
        var primaryCredential = input.CredentialId;
        List<StoredProfile>? profiles = null;
        int? primaryIndex = null;
        if (input.Profiles is { Count: > 0 } inputs)
        {
            profiles = [];
            foreach (var item in inputs)
            {
                var itemModel = item.Model?.Trim();
                await EnsureProviderAsync(item.CredentialId, itemModel, ct);
                profiles.Add(new StoredProfile(item.CredentialId, itemModel!));
            }

            primaryIndex = input.PrimaryIndex ?? 0;
            if (primaryIndex < 0 || primaryIndex >= profiles.Count)
            {
                throw new ArgumentException("Основна модель має бути одним зі списку.", nameof(input));
            }

            primaryCredential = profiles[primaryIndex.Value].CredentialId;
            model = profiles[primaryIndex.Value].Model;
        }
        else if (primaryCredential is { } credentialId)
        {
            await EnsureProviderAsync(credentialId, model, ct);
        }

        var value = JsonSerializer.Serialize(
            new Stored(primaryCredential, string.IsNullOrEmpty(model) ? null : model, profiles, primaryIndex),
            JsonSerializerOptions.Web);
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

    private async Task EnsureProviderAsync(Guid credentialId, string? model, CancellationToken ct)
    {
        var type = await db.Credentials.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.Type).SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("credential", credentialId);
        if (!LlmChat.Providers.Contains(type))
        {
            throw new ArgumentException(
                $"Credential типу «{type}» не підходить для помічника. Оберіть credential типу {string.Join(", ", LlmChat.Providers)}.",
                nameof(credentialId));
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Вкажіть модель помічника, наприклад claude-sonnet-5-5 або gpt-5.", nameof(model));
        }
    }
}
