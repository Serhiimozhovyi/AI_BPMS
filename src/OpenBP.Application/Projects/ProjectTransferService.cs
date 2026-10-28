using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.DataTables;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Application.Workflows;
using OpenBP.Data;
using OpenBP.Engine.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Projects;

/// <summary>Реалізація <see cref="IProjectTransferService"/> поверх сервісів сценаріїв, таблиць і сторінок.</summary>
public sealed partial class ProjectTransferService(
    OpenBpDbContext db,
    ICurrentUser currentUser,
    TimeProvider time,
    IWorkflowGroupService groups,
    IWorkflowService workflows,
    IWorkflowTransferService workflowTransfer,
    IDataTableService tables,
    IProjectPageService pages) : IProjectTransferService
{
    /// <summary>Шаблон не розпізнано або він суперечить собі.</summary>
    public const string BundleInvalidCode = "BUNDLE_INVALID";

    /// <summary>Версія формату шаблону новіша за ту, яку розуміє цей інстанс.</summary>
    public const string VersionUnsupportedCode = "BUNDLE_VERSION_UNSUPPORTED";

    /// <summary>Скільки рядків однієї таблиці найбільше потрапляє в шаблон.</summary>
    public const int MaxExportRows = 10_000;

    private const string RefPrefix = "ref:";
    private const string ExecuteWorkflowType = "core.executeWorkflow";
    private const string DataTableType = "core.dataTable";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <inheritdoc />
    public async Task<JsonObject> ExportAsync(Guid projectId, IReadOnlyCollection<string>? rowsFor, CancellationToken ct)
    {
        currentUser.Require(UserRole.Viewer);
        var access = new ProjectAccess(db, currentUser);
        await access.RequireAsync(projectId, UserRole.Editor, "project", projectId, ct);
        var group = await db.WorkflowGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == projectId, ct)
            ?? throw new NotFoundException("project", projectId);

        var projectTables = (await tables.ListAsync(projectId, ct)).Where(t => t.ProjectId == projectId).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var tableNames = projectTables.ToDictionary(t => t.Id, t => t.Name);
        var withRows = new HashSet<string>(rowsFor ?? [], StringComparer.OrdinalIgnoreCase);
        var tableNodes = new JsonArray();
        foreach (var table in projectTables)
        {
            var node = new JsonObject
            {
                ["name"] = table.Name,
                ["columns"] = JsonSerializer.SerializeToNode(table.Columns, Json),
            };
            if (!table.Constraints.IsEmpty)
            {
                node["constraints"] = JsonSerializer.SerializeToNode(table.Constraints, Json);
            }

            if (withRows.Contains(table.Name))
            {
                node["rows"] = await ExportRowsAsync(table, ct);
            }

            tableNodes.Add(node);
        }

        var entities = await db.Workflows.AsNoTracking().Where(w => w.GroupId == projectId).OrderBy(w => w.Name).ThenBy(w => w.Id).ToListAsync(ct);
        var refs = AssignRefs(entities.Select(e => (e.Id, e.Name)).ToList());
        var refById = refs.ToDictionary(r => r.Id, r => r.Ref);
        var nameById = entities.ToDictionary(e => e.Id, e => e.Name);
        var workflowNodes = new JsonArray();
        var connectors = new Dictionary<string, ManifestConnector>(StringComparer.Ordinal);
        var credentials = new Dictionary<string, ManifestCredential>(StringComparer.Ordinal);
        var variables = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var export = await workflowTransfer.ExportAsync(entity.Id, ct);
            var document = JsonSerializer.SerializeToNode(export.Definition, WorkflowJson.Options)!.AsObject();
            ToPortableReferences(document, refById, tableNames);
            var manifest = export.Manifest with
            {
                Workflows = [.. (export.Manifest.Workflows ?? []).Where(w => !(Guid.TryParse(w.Ref, out var id) && refById.ContainsKey(id)))],
            };
            foreach (var connector in manifest.Connectors)
            {
                connectors.TryAdd(connector.Ref, connector);
            }

            foreach (var credential in manifest.Credentials)
            {
                credentials.TryAdd(credential.Ref, credential);
            }

            variables.UnionWith(manifest.Variables);
            workflowNodes.Add(new JsonObject
            {
                ["ref"] = refById[entity.Id],
                ["export"] = new JsonObject
                {
                    ["definition"] = document,
                    ["manifest"] = JsonSerializer.SerializeToNode(manifest, Json),
                },
            });
        }

        var pageNodes = new JsonArray();
        foreach (var page in await pages.ListAsync(projectId, ct))
        {
            var definition = JsonSerializer.SerializeToNode(page.Definition, Json)!;
            RewritePageWorkflows(definition, nameById);
            pageNodes.Add(new JsonObject
            {
                ["key"] = page.Key,
                ["name"] = page.Name,
                ["kind"] = page.Kind,
                ["definition"] = definition,
                ["roles"] = JsonSerializer.SerializeToNode(page.Roles, Json),
                ["sortOrder"] = page.SortOrder,
            });
        }

        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectExported, AuditEntities.Project, projectId.ToString(),
            new JsonObject { ["name"] = group.Name, ["tables"] = tableNodes.Count, ["workflows"] = workflowNodes.Count, ["pages"] = pageNodes.Count });
        await db.SaveChangesAsync(ct);

        return new JsonObject
        {
            ["formatVersion"] = IProjectTransferService.FormatVersion,
            ["project"] = new JsonObject
            {
                ["name"] = group.Name,
                ["description"] = group.Description,
                ["icon"] = group.Icon,
                ["appRoles"] = JsonSerializer.SerializeToNode(ProjectAppRoles.Parse(group.AppRoles), Json),
            },
            ["tables"] = tableNodes,
            ["pages"] = pageNodes,
            ["workflows"] = workflowNodes,
            ["manifest"] = new JsonObject
            {
                ["connectors"] = JsonSerializer.SerializeToNode(connectors.Values.OrderBy(c => c.Name, StringComparer.Ordinal), Json),
                ["credentials"] = JsonSerializer.SerializeToNode(credentials.Values.OrderBy(c => c.Name, StringComparer.Ordinal), Json),
                ["variables"] = JsonSerializer.SerializeToNode(variables, Json),
            },
        };
    }

    /// <inheritdoc />
    public async Task<ProjectImportResult> ImportAsync(ProjectImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentUser.Require(UserRole.Editor);
        var bundle = Bundle.Parse(request.Bundle);
        var access = new ProjectAccess(db, currentUser);

        Guid? existingProject = request.ProjectId;
        WorkflowGroupEntity? group = null;
        if (existingProject is { } pid)
        {
            await access.RequireAsync(pid, UserRole.Editor, "project", pid, ct);
            group = await db.WorkflowGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == pid, ct) ?? throw new NotFoundException("project", pid);
        }

        // Усі відповідники перевіряються до першої зміни, щоб відмова не лишила частково створений проєкт.
        var mapping = request.Mapping ?? new ImportMapping();
        var unmapped = new List<UnmappedReference>();
        foreach (var workflow in bundle.Workflows)
        {
            var check = new WorkflowImportRequest(PortableCopy(workflow, id => null, keepErrorWorkflow: false), workflow.Manifest, mapping, existingProject);
            foreach (var item in await workflowTransfer.CheckAsync(check, ct))
            {
                if (!unmapped.Any(u => u.Kind == item.Kind && u.Ref == item.Ref))
                {
                    unmapped.Add(item);
                }
            }
        }

        if (unmapped.Count > 0)
        {
            throw new ImportUnmappedException(unmapped);
        }

        var existingWorkflows = existingProject is { } p1
            ? await db.Workflows.AsNoTracking().Where(w => w.GroupId == p1).Select(w => new { w.Id, w.Name }).ToListAsync(ct)
            : [];
        var replace = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var workflow in bundle.Workflows)
        {
            var same = existingWorkflows.Where(w => string.Equals(w.Name, workflow.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count > 1)
            {
                throw new UnprocessableException(BundleInvalidCode,
                    $"У проєкті кілька сценаріїв «{workflow.Name}» — шаблон не знає, який замінити; залиште один.");
            }

            if (same.Count == 1)
            {
                replace[workflow.Ref] = same[0].Id;
            }
        }

        var order = Order(bundle.Workflows);
        var result = new Outcome();
        var createdProject = false;
        Guid projectId;
        if (group is null)
        {
            var name = string.IsNullOrWhiteSpace(request.Name) ? bundle.Project.Name : request.Name.Trim();
            var created = await groups.CreateAsync(name, ct, bundle.Project.Description, bundle.Project.Icon);
            projectId = created.Id;
            createdProject = true;
            result.Name = created.Name;
        }
        else
        {
            projectId = group.Id;
            result.Name = group.Name;
        }

        try
        {
            await ApplyAsync(projectId, group, bundle, mapping, replace, order, result, ct);
        }
        catch when (createdProject)
        {
            await RollbackAsync(projectId, result, CancellationToken.None);
            throw;
        }

        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectImported, AuditEntities.Project, projectId.ToString(),
            new JsonObject
            {
                ["name"] = result.Name,
                ["created"] = createdProject,
                ["tables"] = result.TablesCreated.Count + result.TablesExtended.Count,
                ["pages"] = result.PagesCreated.Count + result.PagesUpdated.Count,
                ["workflows"] = result.WorkflowsCreated.Count + result.WorkflowsUpdated.Count,
            });
        await db.SaveChangesAsync(ct);

        return new ProjectImportResult(
            projectId, result.Name, createdProject, result.TablesCreated, result.TablesExtended, result.RowsInserted,
            result.PagesCreated, result.PagesUpdated, result.WorkflowsCreated, result.WorkflowsUpdated,
            [.. result.MissingVariables.Order(StringComparer.Ordinal)],
            new ImportMapping(result.Connectors, result.Credentials));
    }

    private async Task ApplyAsync(
        Guid projectId,
        WorkflowGroupEntity? group,
        Bundle bundle,
        ImportMapping mapping,
        Dictionary<string, Guid> replace,
        IReadOnlyList<BundleWorkflow> order,
        Outcome result,
        CancellationToken ct)
    {
        // Ролі застосунку: нові ключі додаються, наявні лишаються (учасники вже можуть їх мати).
        var roles = group is null ? [] : ProjectAppRoles.Parse(group.AppRoles).ToList();
        var added = bundle.Project.AppRoles.Where(r => !roles.Any(x => x.Key == r.Key)).ToList();
        if (added.Count > 0)
        {
            await groups.UpdateAsync(projectId, null, null, null, ct, [.. roles, .. added]);
        }

        await ApplyTablesAsync(projectId, bundle, result, ct);

        var ids = new Dictionary<string, Guid>(replace, StringComparer.Ordinal);
        var errorLinks = new List<(BundleWorkflow Workflow, string Target)>();
        foreach (var workflow in order)
        {
            var definition = PortableCopy(workflow, target => ids.TryGetValue(target, out var id) ? id.ToString() : null, keepErrorWorkflow: false);
            var isUpdate = replace.TryGetValue(workflow.Ref, out var replaced);
            var imported = await workflowTransfer.ImportAsync(
                new WorkflowImportRequest(definition, workflow.Manifest, mapping, projectId) { ReplaceWorkflowId = isUpdate ? replaced : null }, ct);
            ids[workflow.Ref] = imported.Workflow.Id;
            (isUpdate ? result.WorkflowsUpdated : result.WorkflowsCreated).Add(imported.Workflow.Name);
            result.MissingVariables.UnionWith(imported.MissingVariables);
            foreach (var pair in imported.Mapping.Connectors ?? new Dictionary<string, string>())
            {
                result.Connectors[pair.Key] = pair.Value;
            }

            foreach (var pair in imported.Mapping.Credentials ?? new Dictionary<string, string>())
            {
                result.Credentials[pair.Key] = pair.Value;
            }

            if (workflow.ErrorWorkflow is { } target)
            {
                errorLinks.Add((workflow, target));
            }
        }

        // Сценарій помилок може вказувати на сценарій, що з'явився пізніше (чи на сам себе), тож посилання ставляться другим проходом.
        foreach (var (workflow, target) in errorLinks)
        {
            var definition = PortableCopy(workflow, id => ids.TryGetValue(id, out var v) ? v.ToString() : null, keepErrorWorkflow: true);
            await workflowTransfer.ImportAsync(
                new WorkflowImportRequest(definition, workflow.Manifest, mapping, projectId) { ReplaceWorkflowId = ids[workflow.Ref] }, ct);
        }

        var existingPages = (await db.ProjectPages.AsNoTracking().Where(p => p.ProjectId == projectId).Select(p => p.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var page in bundle.Pages)
        {
            await pages.UpsertAsync(projectId, page.Key, new ProjectPageInput(page.Name, page.Kind, page.Definition, page.Roles, page.SortOrder), ct);
            (existingPages.Contains(page.Key) ? result.PagesUpdated : result.PagesCreated).Add(page.Key);
        }
    }

    private async Task ApplyTablesAsync(Guid projectId, Bundle bundle, Outcome result, CancellationToken ct)
    {
        var existing = (await tables.ListAsync(projectId, ct)).Where(t => t.ProjectId == projectId).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var bundleNames = bundle.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deferred = new List<(BundleTable Table, Guid Id)>();
        var fresh = new List<(BundleTable Table, Guid Id)>();
        foreach (var table in bundle.Tables)
        {
            if (existing.TryGetValue(table.Name, out var current))
            {
                var missing = table.Columns.Where(c => !current.Columns.Any(x => string.Equals(x.Name, c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0)
                {
                    await tables.UpdateAsync(current.Id, new DataTableDefinition(current.Name, [.. current.Columns, .. missing])
                    {
                        Constraints = current.Constraints,
                        ProjectId = projectId,
                    }, ct);
                    result.TablesExtended.Add(table.Name);
                }

                continue;
            }

            // Колонка `ref` на ще не створену таблицю шаблону додається другим проходом: порядок таблиць не має значення.
            var early = table.Columns.Where(c => !(c.Type == DataColumnType.Ref && c.Table is { } target
                                                   && !string.Equals(target, table.Name, StringComparison.OrdinalIgnoreCase)
                                                   && bundleNames.Contains(target) && !existing.ContainsKey(target))).ToList();
            var split = early.Count != table.Columns.Count;
            var createdTable = await tables.CreateAsync(new DataTableDefinition(table.Name, early)
            {
                Constraints = split ? null : table.Constraints,
                ProjectId = projectId,
            }, ct);
            result.CreatedTableIds.Add(createdTable.Id);
            result.TablesCreated.Add(table.Name);
            fresh.Add((table, createdTable.Id));
            if (split)
            {
                deferred.Add((table, createdTable.Id));
            }
        }

        foreach (var (table, id) in deferred)
        {
            await tables.UpdateAsync(id, new DataTableDefinition(table.Name, table.Columns) { Constraints = table.Constraints, ProjectId = projectId }, ct);
        }

        foreach (var (table, id) in fresh.Where(f => f.Table.Rows.Count > 0))
        {
            foreach (var row in table.Rows)
            {
                await tables.InsertRowAsync(id, row, ct);
            }

            result.RowsInserted[table.Name] = table.Rows.Count;
        }
    }

    /// <summary>Прибирає все, що створив невдалий імпорт у новий проєкт (шаблон не лишає напівстворений проєкт).</summary>
    private async Task RollbackAsync(Guid projectId, Outcome result, CancellationToken ct)
    {
        try
        {
            foreach (var id in await db.Workflows.AsNoTracking().Where(w => w.GroupId == projectId).Select(w => w.Id).ToListAsync(ct))
            {
                await workflows.DeleteAsync(id, ct);
            }

            foreach (var id in result.CreatedTableIds)
            {
                await tables.DeleteAsync(id, ct);
            }

            await groups.DeleteAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is OpenBpException or DbUpdateException)
        {
            // Основна помилка важливіша; залишки видно в проєкті й їх можна видалити вручну.
        }
    }

    private async Task<JsonArray> ExportRowsAsync(DataTableSummary table, CancellationToken ct)
    {
        var sensitive = table.Columns.Where(c => c.Sensitive).Select(c => c.Name).ToList();
        var rows = new JsonArray();
        for (var page = 1; rows.Count < MaxExportRows; page++)
        {
            var batch = await tables.ListRowsAsync(table.Id, null, new PageRequest { Page = page, PageSize = PageRequest.MaxPageSize }, ct);
            foreach (var row in batch.Items)
            {
                var data = (JsonObject)row.Data.DeepClone();
                foreach (var name in sensitive)
                {
                    data.Remove(name);
                }

                rows.Add(data);
            }

            if (batch.Items.Count < PageRequest.MaxPageSize)
            {
                break;
            }
        }

        return rows;
    }

    /// <summary>Стабільні мітки сценаріїв у шаблоні: латинська назва, інакше <c>workflow</c>; повтори отримують числовий суфікс.</summary>
    private static List<(Guid Id, string Ref)> AssignRefs(IReadOnlyList<(Guid Id, string Name)> items)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(Guid, string)>();
        foreach (var (id, name) in items)
        {
            var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
            if (slug.Length == 0)
            {
                slug = "workflow";
            }

            var candidate = slug;
            for (var n = 2; !used.Add(candidate); n++)
            {
                candidate = $"{slug}-{n}";
            }

            result.Add((id, candidate));
        }

        return result;
    }

    /// <summary>
    /// Id сценаріїв проєкту в посиланнях стають мітками <c>ref:…</c>, id таблиць проєкту в <c>core.dataTable</c> — назвами:
    /// після імпорту id будуть інші.
    /// </summary>
    private static void ToPortableReferences(JsonObject document, IReadOnlyDictionary<Guid, string> refById, IReadOnlyDictionary<Guid, string> tableNames)
    {
        if (document["nodes"] is JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                if (node["parameters"] is not JsonObject parameters)
                {
                    continue;
                }

                var type = node["type"]?.GetValue<string>();
                if (type == ExecuteWorkflowType && Text(parameters["workflowId"]) is { } target && Guid.TryParse(target, out var targetId) && refById.TryGetValue(targetId, out var targetRef))
                {
                    parameters["workflowId"] = RefPrefix + targetRef;
                }
                else if (type == DataTableType && Text(parameters["table"]) is { } table && Guid.TryParse(table, out var tableId) && tableNames.TryGetValue(tableId, out var tableName))
                {
                    parameters["table"] = tableName;
                }
            }
        }

        if (document["settings"] is JsonObject settings && Text(settings["errorWorkflowId"]) is { } error
            && Guid.TryParse(error, out var errorId) && refById.TryGetValue(errorId, out var errorRef))
        {
            settings["errorWorkflowId"] = RefPrefix + errorRef;
        }
    }

    /// <summary>У сторінках id сценарію проєкту замінюється його назвою (сценарій шукається за id або назвою).</summary>
    private static void RewritePageWorkflows(JsonNode node, IReadOnlyDictionary<Guid, string> names)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key == "workflow" && Text(obj[key]) is { } text && Guid.TryParse(text, out var id) && names.TryGetValue(id, out var name))
                    {
                        obj[key] = name;
                    }
                    else if (obj[key] is { } child)
                    {
                        RewritePageWorkflows(child, names);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array.OfType<JsonNode>())
                {
                    RewritePageWorkflows(child, names);
                }

                break;
        }
    }

    /// <summary>
    /// Копія визначення сценарію шаблону для імпорту: мітки <c>ref:…</c> у <c>core.executeWorkflow</c> замінює
    /// <paramref name="resolve"/> (невідомі лишає як є), а <c>settings.errorWorkflowId</c> — лише за <paramref name="keepErrorWorkflow"/>.
    /// </summary>
    private static WorkflowDefinition PortableCopy(BundleWorkflow workflow, Func<string, string?> resolve, bool keepErrorWorkflow)
    {
        var document = (JsonObject)workflow.Document.DeepClone();
        if (document["nodes"] is JsonArray nodes)
        {
            foreach (var node in nodes.OfType<JsonObject>())
            {
                if (node["type"]?.GetValue<string>() == ExecuteWorkflowType && node["parameters"] is JsonObject parameters
                    && Text(parameters["workflowId"]) is { } target && target.StartsWith(RefPrefix, StringComparison.Ordinal))
                {
                    parameters["workflowId"] = resolve(target[RefPrefix.Length..]) ?? target;
                }
            }
        }

        if (workflow.ErrorWorkflow is { } error && document["settings"] is JsonObject settings)
        {
            if (keepErrorWorkflow && resolve(error) is { } resolved)
            {
                settings["errorWorkflowId"] = resolved;
            }
            else
            {
                settings.Remove("errorWorkflowId");
            }
        }

        return document.Deserialize<WorkflowDefinition>(WorkflowJson.Options)!;
    }

    /// <summary>Сценарії так, щоб викликаний (<c>core.executeWorkflow</c>) створювався раніше за того, хто викликає.</summary>
    private static IReadOnlyList<BundleWorkflow> Order(IReadOnlyList<BundleWorkflow> items)
    {
        var byRef = items.ToDictionary(w => w.Ref, StringComparer.Ordinal);
        var result = new List<BundleWorkflow>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        void Visit(BundleWorkflow workflow)
        {
            if (state.TryGetValue(workflow.Ref, out var s))
            {
                if (s == 1)
                {
                    throw new UnprocessableException(BundleInvalidCode, $"Сценарії шаблону викликають один одного по колу (через «{workflow.Name}»).");
                }

                return;
            }

            state[workflow.Ref] = 1;
            foreach (var target in workflow.Calls)
            {
                Visit(byRef[target]);
            }

            state[workflow.Ref] = 2;
            result.Add(workflow);
        }

        foreach (var workflow in items)
        {
            Visit(workflow);
        }

        return result;
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();

    private sealed class Outcome
    {
        public string Name { get; set; } = "";

        public List<string> TablesCreated { get; } = [];

        public List<string> TablesExtended { get; } = [];

        public Dictionary<string, int> RowsInserted { get; } = new(StringComparer.Ordinal);

        public List<string> PagesCreated { get; } = [];

        public List<string> PagesUpdated { get; } = [];

        public List<string> WorkflowsCreated { get; } = [];

        public List<string> WorkflowsUpdated { get; } = [];

        public SortedSet<string> MissingVariables { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Connectors { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Credentials { get; } = new(StringComparer.Ordinal);

        public List<Guid> CreatedTableIds { get; } = [];
    }

    private sealed record BundleProject(string Name, string Description, string Icon, IReadOnlyList<ProjectAppRole> AppRoles);

    private sealed record BundleTable(string Name, IReadOnlyList<DataColumn> Columns, DataTableConstraints? Constraints, IReadOnlyList<JsonObject> Rows);

    private sealed record BundlePage(string Key, string Name, string Kind, PageDefinition Definition, IReadOnlyList<string> Roles, int? SortOrder);

    private sealed record BundleWorkflow(string Ref, string Name, JsonObject Document, WorkflowManifest Manifest)
    {
        /// <summary>Мітки сценаріїв, які цей викликає через <c>core.executeWorkflow</c>.</summary>
        public IReadOnlyList<string> Calls { get; init; } = [];

        /// <summary>Мітка сценарію помилок з цього шаблону.</summary>
        public string? ErrorWorkflow { get; init; }
    }

    private sealed record Bundle(
        BundleProject Project,
        IReadOnlyList<BundleTable> Tables,
        IReadOnlyList<BundlePage> Pages,
        IReadOnlyList<BundleWorkflow> Workflows)
    {
        public static Bundle Parse(JsonObject? json)
        {
            if (json is null)
            {
                throw Invalid("Потрібне поле bundle.");
            }

            try
            {
                var version = json["formatVersion"]?.GetValue<int>() ?? throw Invalid("У шаблоні немає formatVersion.");
                if (version > IProjectTransferService.FormatVersion || version < 1)
                {
                    throw new UnprocessableException(VersionUnsupportedCode,
                        $"Формат шаблону {version} не підтримується (цей інстанс розуміє {IProjectTransferService.FormatVersion}).");
                }

                var project = json["project"] as JsonObject ?? throw Invalid("У шаблоні немає project.");
                var name = project["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw Invalid("У project потрібна назва.");
                }

                var roles = project["appRoles"] is { } r ? r.Deserialize<List<ProjectAppRole>>(Json) ?? [] : [];
                var parsedTables = new List<BundleTable>();
                foreach (var table in (json["tables"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var tableName = table["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(tableName) || parsedTables.Any(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw Invalid($"Таблиця «{tableName}» без назви або повторюється.");
                    }

                    parsedTables.Add(new BundleTable(
                        tableName,
                        table["columns"].Deserialize<List<DataColumn>>(Json) ?? [],
                        table["constraints"]?.Deserialize<DataTableConstraints>(Json),
                        [.. (table["rows"] as JsonArray)?.OfType<JsonObject>().Select(x => (JsonObject)x.DeepClone()) ?? []]));
                }

                var parsedPages = new List<BundlePage>();
                foreach (var page in (json["pages"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var key = page["key"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(key) || parsedPages.Any(p => p.Key == key))
                    {
                        throw Invalid($"Сторінка «{key}» без ключа або повторюється.");
                    }

                    parsedPages.Add(new BundlePage(
                        key,
                        page["name"]?.GetValue<string>() ?? key,
                        page["kind"]?.GetValue<string>() ?? "",
                        page["definition"].Deserialize<PageDefinition>(Json) ?? new PageDefinition(),
                        page["roles"]?.Deserialize<List<string>>(Json) ?? [],
                        page["sortOrder"]?.GetValue<int>()));
                }

                var parsedWorkflows = new List<BundleWorkflow>();
                foreach (var item in (json["workflows"] as JsonArray)?.OfType<JsonObject>() ?? [])
                {
                    var reference = item["ref"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(reference) || parsedWorkflows.Any(w => w.Ref == reference))
                    {
                        throw Invalid($"Сценарій із міткою «{reference}» без мітки або повторюється.");
                    }

                    var export = item["export"] as JsonObject ?? throw Invalid($"У сценарію «{reference}» немає export.");
                    var document = export["definition"] as JsonObject ?? throw Invalid($"У сценарію «{reference}» немає definition.");
                    var workflowName = document["name"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(workflowName))
                    {
                        throw Invalid($"У сценарію «{reference}» немає назви.");
                    }

                    var manifest = export["manifest"]?.Deserialize<WorkflowManifest>(Json) ?? new WorkflowManifest([], [], []);
                    var calls = new List<string>();
                    foreach (var node in (document["nodes"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    {
                        if (node["type"]?.GetValue<string>() == ExecuteWorkflowType && Text(node["parameters"]?["workflowId"]) is { } target
                            && target.StartsWith(RefPrefix, StringComparison.Ordinal))
                        {
                            calls.Add(target[RefPrefix.Length..]);
                        }
                    }

                    var error = Text(document["settings"]?["errorWorkflowId"]);
                    parsedWorkflows.Add(new BundleWorkflow(reference, workflowName, document, manifest)
                    {
                        Calls = [.. calls.Distinct(StringComparer.Ordinal)],
                        ErrorWorkflow = error is not null && error.StartsWith(RefPrefix, StringComparison.Ordinal) ? error[RefPrefix.Length..] : null,
                    });
                }

                var known = parsedWorkflows.Select(w => w.Ref).ToHashSet(StringComparer.Ordinal);
                foreach (var workflow in parsedWorkflows)
                {
                    var unknown = workflow.Calls.Concat(workflow.ErrorWorkflow is null ? [] : [workflow.ErrorWorkflow]).FirstOrDefault(c => !known.Contains(c));
                    if (unknown is not null)
                    {
                        throw Invalid($"Сценарій «{workflow.Name}» посилається на «{unknown}», якого немає в шаблоні.");
                    }
                }

                return new Bundle(
                    new BundleProject(name.Trim(), project["description"]?.GetValue<string>() ?? "", project["icon"]?.GetValue<string>() ?? "", roles),
                    parsedTables,
                    parsedPages,
                    parsedWorkflows);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                throw Invalid($"Шаблон має неправильну форму: {ex.Message}");
            }
        }

        private static UnprocessableException Invalid(string message) => new(BundleInvalidCode, message);
    }
}
