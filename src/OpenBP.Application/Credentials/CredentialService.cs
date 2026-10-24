using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Credentials;

/// <summary>
/// Реалізація <see cref="ICredentialService"/> над <c>credentials</c> і <see cref="CredentialStore"/> (AES-256-GCM).
/// Розшифровує дані лише щоб перелічити заповнені поля і злити часткову заміну; значення з сервісу не виходять.
/// </summary>
/// <param name="db">Контекст БД.</param>
/// <param name="currentUser">Користувач запиту.</param>
/// <param name="store">Сховище (без оновлення oauth2-токенів: перелік полів не має звертатися до провайдера).</param>
/// <param name="time">Годинник для стану oauth2; <c>null</c> — системний.</param>
public sealed class CredentialService(OpenBpDbContext db, ICurrentUser currentUser, CredentialStore store, TimeProvider? time = null) : ICredentialService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Код конфлікту: credential використовують конектори або сценарії.</summary>
    public const string CredentialInUseCode = "CREDENTIAL_IN_USE";

    private const string Entity = "credential";

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <inheritdoc />
    public IReadOnlyList<CredentialTypeInfo> Types()
    {
        currentUser.Require(UserRole.Viewer);
        return CredentialTypes.All;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialSummary>> ListAsync(CancellationToken ct)
    {
        var scope = await _access.ScopeAsync(ct);
        // Не-адмін бачить лише credentials проєктів, де він editor (F6-14).
        var managed = scope.Projects.Where(p => p.Value >= UserRole.Editor).Select(p => p.Key).ToList();
        if (!scope.All && managed.Count == 0)
        {
            throw new ForbiddenException(UserRole.Admin);
        }

        var rows = await db.Credentials.AsNoTracking()
            .Where(c => scope.All || (c.ProjectId != null && managed.Contains(c.ProjectId.Value)))
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.Type, c.CreatedBy, c.ProjectId }).ToListAsync(ct);
        var usage = await UsageAsync(ct);
        var result = new List<CredentialSummary>(rows.Count);
        foreach (var r in rows)
        {
            result.Add(await SummaryAsync(r.Id, r.Name, r.Type, r.CreatedBy, usage, ct) with { ProjectId = r.ProjectId });
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialOption>> OptionsAsync(string? type, CancellationToken ct, Guid? projectId = null)
    {
        var scope = await _access.ScopeAsync(ct);
        if (!scope.All && scope.SharedRole < UserRole.Editor && !scope.Projects.Values.Any(r => r >= UserRole.Editor))
        {
            throw new ForbiddenException(UserRole.Editor);
        }

        if (projectId is not null && scope.RoleIn(projectId) is null)
        {
            throw new NotFoundException("workflow-group", projectId);
        }

        var visible = projectId is { } only ? [only] : scope.All ? null : scope.Projects.Keys.ToList();
        var query = db.Credentials.AsNoTracking();
        if (visible is not null)
        {
            query = query.Where(c => c.ProjectId == null || visible.Contains(c.ProjectId.Value));
        }

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(c => c.Type == type);
        }

        return await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Select(c => new CredentialOption(c.Id, c.Name, c.Type, c.ProjectId)).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<CredentialSummary> GetAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var row = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireManageAsync(row.ProjectId, Entity, id, ct);
        return await SummaryAsync(row.Id, row.Name, row.Type, row.CreatedBy, await UsageAsync(ct), ct) with { ProjectId = row.ProjectId };
    }

    /// <inheritdoc />
    public async Task<CredentialSummary> CreateAsync(CredentialCreateInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        ArgumentNullException.ThrowIfNull(input);
        if (input.ProjectId is { } project && !await db.WorkflowGroups.AnyAsync(g => g.Id == project, ct))
        {
            throw new NotFoundException("workflow-group", project);
        }

        await _access.RequireManageAsync(input.ProjectId, "workflow-group", input.ProjectId?.ToString() ?? "none", ct);
        var name = RequireName(input.Name);
        if (string.IsNullOrEmpty(input.Type) || CredentialTypes.Find(input.Type) is null)
        {
            throw new CredentialValidationException([new("type", $"Невідомий тип «{input.Type}».")]);
        }

        var data = input.Data ?? throw new CredentialValidationException([new("data", "Потрібні дані.")]);
        RejectManaged(input.Type, data);
        CredentialTypes.Validate(input.Type, data);
        var id = await store.CreateAsync(name, input.Type, data, currentUser.UserId, ct, input.ProjectId);
        // Значення секретів у журнал не потрапляють: лише назва, тип і імена полів.
        db.Audit(currentUser, _time.GetUtcNow(), AuditActions.CredentialCreated, AuditEntities.Credential, id.ToString(),
            new JsonObject { ["name"] = name, ["type"] = input.Type, ["fields"] = FieldNames(data) });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<CredentialSummary> UpdateAsync(Guid id, CredentialUpdateInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        ArgumentNullException.ThrowIfNull(input);
        var row = await db.Credentials.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireManageAsync(row.ProjectId, Entity, id, ct);
        if (input.Name is not null)
        {
            var previous = row.Name;
            row.Name = RequireName(input.Name);
            if (previous != row.Name)
            {
                db.Audit(currentUser, _time.GetUtcNow(), AuditActions.CredentialRenamed, AuditEntities.Credential, id.ToString(),
                    new JsonObject { ["name"] = row.Name, ["previousName"] = previous, ["type"] = row.Type });
            }

            await db.SaveChangesAsync(ct);
        }

        if (input.Data is not null)
        {
            var current = await store.GetDataAsync(id, ct) ?? throw new NotFoundException(Entity, id);
            RejectManaged(row.Type, input.Data);
            var merged = CredentialTypes.Merge(current, input.Data);
            if (row.Type == OAuth2Credential.Type && OAuth2Credential.ClientFields.Any(input.Data.ContainsKey))
            {
                // Інший клієнт чи scopes — отримані токени вже не відповідають налаштуванням.
                foreach (var managed in OAuth2Credential.ManagedFields)
                {
                    merged.Remove(managed);
                }
            }

            CredentialTypes.Validate(row.Type, merged);
            await store.UpdateDataAsync(id, merged, ct);
            db.Audit(currentUser, _time.GetUtcNow(), AuditActions.CredentialReplaced, AuditEntities.Credential, id.ToString(),
                new JsonObject { ["name"] = row.Name, ["type"] = row.Type, ["fields"] = FieldNames(input.Data) });
            await db.SaveChangesAsync(ct);
        }

        return await GetAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var row = await db.Credentials.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireManageAsync(row.ProjectId, Entity, id, ct);
        var usage = Of(await UsageAsync(ct), id);
        if (usage.Any)
        {
            var names = usage.Connectors.Select(c => $"конектор «{c.Name}»").Concat(usage.Workflows.Select(w => $"сценарій «{w.Name}»"));
            throw new ConflictException(CredentialInUseCode, $"Облікові дані «{row.Name}» використовують: {string.Join(", ", names)}.");
        }

        db.Credentials.Remove(row);
        db.Audit(currentUser, _time.GetUtcNow(), AuditActions.CredentialDeleted, AuditEntities.Credential, id.ToString(),
            new JsonObject { ["name"] = row.Name, ["type"] = row.Type });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Імена переданих полів (без значень) — для журналу аудиту.</summary>
    private static JsonArray FieldNames(JsonObject data) => [.. data.Select(p => (JsonNode?)JsonValue.Create(p.Key)).OrderBy(n => (string)n!, StringComparer.Ordinal)];

    private async Task<CredentialSummary> SummaryAsync(Guid id, string name, string type, Guid? createdBy, UsageIndex usage, CancellationToken ct)
    {
        IReadOnlyList<string> fields = [];
        string? error = null;
        CredentialOAuthStatus? oauth = null;
        try
        {
            var data = await store.GetDataAsync(id, ct);
            if (data is not null)
            {
                fields = CredentialTypes.FilledFields(data);
                oauth = type == OAuth2Credential.Type ? OAuthStatus(data, _time.GetUtcNow()) : null;
            }
        }
        catch (CredentialDecryptionException)
        {
            error = "Дані не розшифровуються: ймовірно, змінився ключ шифрування OPENBP_ENCRYPTION_KEY.";
        }

        return new CredentialSummary(id, name, type, fields, createdBy, Of(usage, id), error, oauth);
    }

    /// <summary>Стан oauth2-credential'а за його даними.</summary>
    /// <param name="data">Розшифровані дані.</param>
    /// <param name="now">Поточний час.</param>
    public static CredentialOAuthStatus OAuthStatus(JsonObject data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        var expiresAt = OAuth2Credential.ExpiresAt(data);
        var error = OAuth2Credential.Text(data, "oauthError");
        if (string.IsNullOrEmpty(OAuth2Credential.Text(data, "accessToken")))
        {
            return new("notConnected", null, error);
        }

        if (!string.IsNullOrEmpty(error))
        {
            return new(expiresAt is { } e && e <= now ? "expired" : "error", expiresAt, error);
        }

        var refreshable = !string.IsNullOrEmpty(OAuth2Credential.Text(data, "refreshToken"));
        return new(expiresAt is { } at && at <= now && !refreshable ? "expired" : "connected", expiresAt, null);
    }

    private static void RejectManaged(string type, JsonObject data)
    {
        if (type != OAuth2Credential.Type)
        {
            return;
        }

        var managed = data.Select(p => p.Key).Where(OAuth2Credential.ManagedFields.Contains).ToList();
        if (managed.Count > 0)
        {
            throw new CredentialValidationException([.. managed.Select(f => new CredentialFieldError(f, "Поле заповнює OpenBP після «Підключити»."))]);
        }
    }

    private static CredentialUsage Of(UsageIndex usage, Guid id) => new(
        usage.Connectors.Where(c => c.CredentialId == id).Select(c => new CredentialReference(c.Id, c.Name)).ToList(),
        usage.Workflows.Where(w => w.Ids.Contains(id)).Select(w => new CredentialReference(w.Id, w.Name)).ToList());

    private async Task<UsageIndex> UsageAsync(CancellationToken ct)
    {
        var connectors = await db.McpServers.AsNoTracking().Where(s => s.CredentialId != null)
            .OrderBy(s => s.Name).Select(s => new ConnectorRef(s.Id, s.Name, s.CredentialId!.Value)).ToListAsync(ct);
        var definitions = await db.Workflows.AsNoTracking()
            .Join(db.WorkflowVersions, w => w.CurrentVersionId, v => v.Id, (w, v) => new { w.Id, w.Name, v.Definition })
            .OrderBy(w => w.Name)
            .ToListAsync(ct);
        var workflows = definitions.Select(d => new WorkflowRef(d.Id, d.Name, GuidsIn(d.Definition))).Where(w => w.Ids.Count > 0).ToList();
        return new UsageIndex(connectors, workflows);
    }

    /// <summary>Усі рядки-UUID у параметрах вузлів: <c>credentialId</c> вузлів, <c>auth.credentialId</c> webhook-а тощо.</summary>
    private static HashSet<Guid> GuidsIn(string definition)
    {
        var result = new HashSet<Guid>();
        JsonNode? document;
        try
        {
            document = JsonNode.Parse(definition);
        }
        catch (JsonException)
        {
            return result;
        }

        if (document?["nodes"] is JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                Collect(node["parameters"], result);
            }
        }

        return result;
    }

    private static void Collect(JsonNode? node, HashSet<Guid> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, v) in obj)
                {
                    Collect(v, result);
                }

                break;
            case JsonArray arr:
                foreach (var v in arr)
                {
                    Collect(v, result);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g):
                result.Add(g);
                break;
        }
    }

    private static string RequireName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new CredentialValidationException([new("name", "Назва не може бути порожньою.")])
            : name.Trim();

    private sealed record ConnectorRef(Guid Id, string Name, Guid CredentialId);

    private sealed record WorkflowRef(Guid Id, string Name, HashSet<Guid> Ids);

    private sealed record UsageIndex(IReadOnlyList<ConnectorRef> Connectors, IReadOnlyList<WorkflowRef> Workflows);
}
