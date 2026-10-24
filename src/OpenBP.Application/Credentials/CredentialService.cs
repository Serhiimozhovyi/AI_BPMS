using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Application.Credentials;

/// <summary>
/// Реалізація <see cref="ICredentialService"/> над <c>credentials</c> і <see cref="CredentialStore"/> (AES-256-GCM).
/// Розшифровує дані лише щоб перелічити заповнені поля і злити часткову заміну; значення з сервісу не виходять.
/// </summary>
public sealed class CredentialService(OpenBpDbContext db, ICurrentUser currentUser, CredentialStore store) : ICredentialService
{
    /// <summary>Код конфлікту: credential використовують конектори або сценарії.</summary>
    public const string CredentialInUseCode = "CREDENTIAL_IN_USE";

    private const string Entity = "credential";

    /// <inheritdoc />
    public IReadOnlyList<CredentialTypeInfo> Types()
    {
        currentUser.Require(UserRole.Admin);
        return CredentialTypes.All;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialSummary>> ListAsync(CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var rows = await db.Credentials.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.Type, c.CreatedBy }).ToListAsync(ct);
        var usage = await UsageAsync(ct);
        var result = new List<CredentialSummary>(rows.Count);
        foreach (var r in rows)
        {
            result.Add(await SummaryAsync(r.Id, r.Name, r.Type, r.CreatedBy, usage, ct));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<CredentialSummary> GetAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var row = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        return await SummaryAsync(row.Id, row.Name, row.Type, row.CreatedBy, await UsageAsync(ct), ct);
    }

    /// <inheritdoc />
    public async Task<CredentialSummary> CreateAsync(CredentialCreateInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        ArgumentNullException.ThrowIfNull(input);
        var name = RequireName(input.Name);
        if (string.IsNullOrEmpty(input.Type) || CredentialTypes.Find(input.Type) is null)
        {
            throw new CredentialValidationException([new("type", $"Невідомий тип «{input.Type}».")]);
        }

        var data = input.Data ?? throw new CredentialValidationException([new("data", "Потрібні дані.")]);
        CredentialTypes.Validate(input.Type, data);
        var id = await store.CreateAsync(name, input.Type, data, currentUser.UserId, ct);
        return await GetAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task<CredentialSummary> UpdateAsync(Guid id, CredentialUpdateInput input, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        ArgumentNullException.ThrowIfNull(input);
        var row = await db.Credentials.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        if (input.Name is not null)
        {
            row.Name = RequireName(input.Name);
            await db.SaveChangesAsync(ct);
        }

        if (input.Data is not null)
        {
            var current = await store.GetDataAsync(id, ct) ?? throw new NotFoundException(Entity, id);
            var merged = CredentialTypes.Merge(current, input.Data);
            CredentialTypes.Validate(row.Type, merged);
            await store.UpdateDataAsync(id, merged, ct);
        }

        return await GetAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Admin);
        var row = await db.Credentials.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        var usage = Of(await UsageAsync(ct), id);
        if (usage.Any)
        {
            var names = usage.Connectors.Select(c => $"конектор «{c.Name}»").Concat(usage.Workflows.Select(w => $"сценарій «{w.Name}»"));
            throw new ConflictException(CredentialInUseCode, $"Облікові дані «{row.Name}» використовують: {string.Join(", ", names)}.");
        }

        db.Credentials.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    private async Task<CredentialSummary> SummaryAsync(Guid id, string name, string type, Guid? createdBy, UsageIndex usage, CancellationToken ct)
    {
        IReadOnlyList<string> fields = [];
        string? error = null;
        try
        {
            var data = await store.GetDataAsync(id, ct);
            if (data is not null)
            {
                fields = CredentialTypes.FilledFields(data);
            }
        }
        catch (CredentialDecryptionException)
        {
            error = "Дані не розшифровуються: ймовірно, змінився ключ шифрування OPENBP_ENCRYPTION_KEY.";
        }

        return new CredentialSummary(id, name, type, fields, createdBy, Of(usage, id), error);
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
