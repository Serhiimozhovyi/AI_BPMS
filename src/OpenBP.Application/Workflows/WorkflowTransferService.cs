using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Variables;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Реалізація <see cref="IWorkflowTransferService"/>; створення і валідацію робить <see cref="IWorkflowService"/>.</summary>
public sealed class WorkflowTransferService(OpenBpDbContext db, ICurrentUser currentUser, IWorkflowService workflows)
    : IWorkflowTransferService
{
    private const string ServerParameter = "serverId";
    private const string CredentialParameter = "credentialId";

    /// <inheritdoc />
    public async Task<WorkflowExport> ExportAsync(Guid id, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var detail = await workflows.GetAsync(id, ct);
        await new ProjectAccess(db, currentUser).RequireAsync(detail.GroupId, UserRole.Editor, "workflow", id, ct);
        var document = ToDocument(detail.Definition);
        var refs = References.Collect(document);

        var serverIds = ParseIds(refs.Connectors);
        var servers = await db.McpServers.AsNoTracking().Where(s => serverIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.Transport }).ToListAsync(ct);
        var credentialIds = ParseIds(refs.Credentials);
        var credentials = await db.Credentials.AsNoTracking().Where(c => credentialIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Type }).ToListAsync(ct);
        var workflowIds = ParseIds(refs.Workflows);
        var others = await db.Workflows.AsNoTracking().Where(w => workflowIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Name }).ToListAsync(ct);

        var manifest = new WorkflowManifest(
            [.. servers.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new ManifestConnector(s.Id.ToString(), s.Name, s.Transport))],
            [.. credentials.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new ManifestCredential(c.Id.ToString(), c.Name, c.Type))],
            VariableReferences.Keys(document),
            [.. others.Select(w => new ManifestWorkflow(w.Id.ToString(), w.Name))]);
        return new WorkflowExport(detail.Definition, manifest);
    }

    /// <inheritdoc />
    public async Task<WorkflowImportResult> ImportAsync(WorkflowImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentUser.Require(UserRole.Viewer);
        var definition = request.Definition ?? throw new ArgumentException("Потрібне поле definition.", nameof(request));
        var document = ToDocument(definition);
        var refs = References.Collect(document);
        var manifest = request.Manifest ?? new WorkflowManifest([], [], []);
        var mapping = request.Mapping ?? new ImportMapping();
        var unmapped = new List<UnmappedReference>();

        // Перемаплювати можна лише на спільні ресурси та ресурси проєкту, куди імпортуємо (F6-14).
        var project = request.GroupId ?? currentUser.ApiKeyProjectId;
        var servers = await db.McpServers.AsNoTracking().Where(s => s.ProjectId == null || s.ProjectId == project)
            .Select(s => new Target(s.Id, s.Name, s.Transport)).ToListAsync(ct);
        var credentials = await db.Credentials.AsNoTracking().Where(c => c.ProjectId == null || c.ProjectId == project)
            .Select(c => new Target(c.Id, c.Name, c.Type)).ToListAsync(ct);
        var targetWorkflows = refs.Workflows.Count == 0
            ? []
            : await (await new ProjectAccess(db, currentUser).VisibleWorkflowsAsync(db.Workflows.AsNoTracking(), ct))
                .Select(w => new Target(w.Id, w.Name, null)).ToListAsync(ct);

        var connectorMap = Resolve(
            ReferenceKinds.Connector,
            refs.Connectors,
            manifest.Connectors.ToDictionary(c => c.Ref, c => (c.Name, (string?)c.Transport), StringComparer.Ordinal),
            mapping.Connectors,
            servers,
            unmapped);
        var credentialMap = Resolve(
            ReferenceKinds.Credential,
            refs.Credentials,
            manifest.Credentials.ToDictionary(c => c.Ref, c => (c.Name, (string?)c.Type), StringComparer.Ordinal),
            mapping.Credentials,
            credentials,
            unmapped);
        var workflowMap = Resolve(
            ReferenceKinds.Workflow,
            refs.Workflows,
            (manifest.Workflows ?? []).ToDictionary(w => w.Ref, w => (w.Name, (string?)null), StringComparer.Ordinal),
            mapping.Workflows,
            targetWorkflows,
            unmapped);
        if (unmapped.Count > 0)
        {
            throw new ImportUnmappedException(unmapped);
        }

        References.Rewrite(document, connectorMap, credentialMap, workflowMap);
        var created = await workflows.CreateAsync(document.Deserialize<WorkflowDefinition>(WorkflowJson.Options)!, request.GroupId, ct);

        var keys = VariableReferences.Keys(document);
        var known = keys.Count == 0
            ? []
            : await db.Variables.AsNoTracking().Where(v => keys.Contains(v.Key)).Select(v => v.Key).ToListAsync(ct);
        return new WorkflowImportResult(
            created,
            new ImportMapping(connectorMap, credentialMap, workflowMap),
            [.. keys.Where(k => !known.Contains(k, StringComparer.Ordinal))]);
    }

    private static Dictionary<string, string> Resolve(
        string kind,
        IReadOnlyCollection<string> refs,
        Dictionary<string, (string Name, string? Type)> manifest,
        IReadOnlyDictionary<string, string>? explicitMap,
        IReadOnlyList<Target> targets,
        List<UnmappedReference> unmapped)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in refs)
        {
            var described = manifest.TryGetValue(reference, out var d) ? d : ((string Name, string? Type)?)null;
            if (explicitMap is not null && explicitMap.TryGetValue(reference, out var chosen))
            {
                if (Guid.TryParse(chosen, out var chosenId) && targets.Any(t => t.Id == chosenId))
                {
                    result[reference] = chosenId.ToString();
                }
                else
                {
                    unmapped.Add(new UnmappedReference(kind, reference, described?.Name, described?.Type, "invalid"));
                }

                continue;
            }

            if (described is { } info)
            {
                var matches = targets
                    .Where(t => string.Equals(t.Name, info.Name, StringComparison.OrdinalIgnoreCase)
                                && (info.Type is null || string.Equals(t.Type, info.Type, StringComparison.Ordinal)))
                    .ToList();
                if (matches.Count == 1)
                {
                    result[reference] = matches[0].Id.ToString();
                }
                else
                {
                    unmapped.Add(new UnmappedReference(kind, reference, info.Name, info.Type, matches.Count == 0 ? "not_found" : "ambiguous"));
                }

                continue;
            }

            // Без маніфесту посилання має вже бути id цього середовища.
            if (Guid.TryParse(reference, out var id) && targets.Any(t => t.Id == id))
            {
                result[reference] = reference;
            }
            else
            {
                unmapped.Add(new UnmappedReference(kind, reference, null, null, "not_found"));
            }
        }

        return result;
    }

    private static List<Guid> ParseIds(IEnumerable<string> refs) =>
        [.. refs.Select(r => Guid.TryParse(r, out var id) ? id : (Guid?)null).OfType<Guid>()];

    private static JsonObject ToDocument(WorkflowDefinition definition) =>
        JsonSerializer.SerializeToNode(definition, WorkflowJson.Options)!.AsObject();

    private sealed record Target(Guid Id, string Name, string? Type);

    /// <summary>Посилання сценарію на інші об'єкти: <c>serverId</c>/<c>credentialId</c> будь-де в параметрах вузлів, <c>settings.errorWorkflowId</c>.</summary>
    private sealed record References(IReadOnlyCollection<string> Connectors, IReadOnlyCollection<string> Credentials, IReadOnlyCollection<string> Workflows)
    {
        public static References Collect(JsonObject document)
        {
            var connectors = new SortedSet<string>(StringComparer.Ordinal);
            var credentials = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var parameters in Parameters(document))
            {
                Walk(parameters, (name, value) =>
                {
                    if (name == ServerParameter)
                    {
                        connectors.Add(value);
                    }
                    else if (name == CredentialParameter)
                    {
                        credentials.Add(value);
                    }

                    return null;
                });
            }

            var workflows = new SortedSet<string>(StringComparer.Ordinal);
            if (ErrorWorkflow(document) is { } errorWorkflow)
            {
                workflows.Add(errorWorkflow);
            }

            return new References(connectors, credentials, workflows);
        }

        public static void Rewrite(
            JsonObject document,
            Dictionary<string, string> connectors,
            Dictionary<string, string> credentials,
            Dictionary<string, string> workflows)
        {
            foreach (var parameters in Parameters(document))
            {
                Walk(parameters, (name, value) => name switch
                {
                    ServerParameter when connectors.TryGetValue(value, out var to) => to,
                    CredentialParameter when credentials.TryGetValue(value, out var to) => to,
                    _ => null,
                });
            }

            if (ErrorWorkflow(document) is { } errorWorkflow && workflows.TryGetValue(errorWorkflow, out var target))
            {
                document["settings"]!["errorWorkflowId"] = target;
            }
        }

        private static IEnumerable<JsonNode> Parameters(JsonObject document) =>
            document["nodes"] is JsonArray nodes
                ? nodes.OfType<JsonObject>().Select(n => n["parameters"]).OfType<JsonNode>()
                : [];

        private static string? ErrorWorkflow(JsonObject document) =>
            document["settings"]?["errorWorkflowId"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

        /// <summary>Обходить рядкові властивості; якщо <paramref name="visit"/> повертає не <c>null</c>, значення замінюється.</summary>
        private static void Walk(JsonNode node, Func<string, string, string?> visit)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var name in obj.Select(p => p.Key).ToList())
                    {
                        if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            if (visit(name, text) is { } replaced)
                            {
                                obj[name] = replaced;
                            }
                        }
                        else if (obj[name] is { } child)
                        {
                            Walk(child, visit);
                        }
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array.OfType<JsonNode>())
                    {
                        Walk(child, visit);
                    }

                    break;
            }
        }
    }
}
