using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Data;
using OpenBP.Shared.Validation;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Перевірка, що сценарій проєкту посилається лише на спільні конектори й credentials або на ресурси свого проєкту
/// (F6-14). Посилання — <c>serverId</c> і <c>credentialId</c> будь-де в параметрах вузлів (як в експорті F6-09) та id
/// таблиці в параметрі <c>table</c> вузла <c>core.dataTable</c> (F8-02); вирази, назви й невідомі id тут не
/// перевіряються — це справа решти валідатора (таблицю за назвою з іншого проєкту вузол просто не знайде).
/// </summary>
internal static class ProjectReferences
{
    private const string ServerParameter = "serverId";
    private const string CredentialParameter = "credentialId";
    private const string TableParameter = "table";
    private const string DataTableNode = "core.dataTable";

    /// <summary>Помилки <see cref="WorkflowValidationCodes.ProjectReferenceForbidden"/> для документа сценарію проєкту <paramref name="projectId"/>.</summary>
    /// <param name="db">Контекст БД.</param>
    /// <param name="document">Документ сценарію.</param>
    /// <param name="projectId">Проєкт сценарію; <c>null</c> — спільний простір.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<IReadOnlyList<WorkflowValidationError>> ValidateAsync(
        OpenBpDbContext db, JsonObject document, Guid? projectId, CancellationToken ct)
    {
        var references = Find(document);
        if (references.Count == 0)
        {
            return [];
        }

        var serverIds = references.Where(r => r.Name == ServerParameter).Select(r => r.Id).Distinct().ToList();
        var credentialIds = references.Where(r => r.Name == CredentialParameter).Select(r => r.Id).Distinct().ToList();
        var servers = serverIds.Count == 0
            ? []
            : await db.McpServers.AsNoTracking().Where(s => serverIds.Contains(s.Id) && s.ProjectId != null && s.ProjectId != projectId)
                .Select(s => new { s.Id, s.Name }).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var credentials = credentialIds.Count == 0
            ? []
            : await db.Credentials.AsNoTracking().Where(c => credentialIds.Contains(c.Id) && c.ProjectId != null && c.ProjectId != projectId)
                .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var tableIds = references.Where(r => r.Name == TableParameter).Select(r => r.Id).Distinct().ToList();
        var tables = tableIds.Count == 0
            ? []
            : await db.DataTables.AsNoTracking().Where(t => tableIds.Contains(t.Id) && t.ProjectId != null && t.ProjectId != projectId)
                .Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var errors = new List<WorkflowValidationError>();
        foreach (var reference in references)
        {
            var foreign = reference.Name switch
            {
                ServerParameter => servers,
                CredentialParameter => credentials,
                _ => tables,
            };
            if (foreign.TryGetValue(reference.Id, out var name))
            {
                var what = reference.Name switch
                {
                    ServerParameter => "Конектор",
                    CredentialParameter => "Обліковий запис",
                    _ => "Таблиця",
                };
                errors.Add(new WorkflowValidationError(
                    WorkflowValidationCodes.ProjectReferenceForbidden,
                    $"{what} «{name}» належить іншому проєкту; сценарій може використовувати лише спільні ресурси і ресурси свого проєкту.",
                    reference.NodeId,
                    reference.Path));
            }
        }

        return errors;
    }

    private static List<Reference> Find(JsonObject document)
    {
        var result = new List<Reference>();
        if (document["nodes"] is not JsonArray nodes)
        {
            return result;
        }

        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is JsonObject node && node["id"]?.GetValue<string>() is { } nodeId && node["parameters"] is { } parameters)
            {
                Walk(parameters, $"/nodes/{i}/parameters", nodeId, result);
                if (node["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && typeName == DataTableNode
                    && parameters[TableParameter] is JsonValue table && table.TryGetValue<string>(out var tableText) && Guid.TryParse(tableText, out var tableId))
                {
                    result.Add(new Reference(TableParameter, tableId, nodeId, $"/nodes/{i}/parameters/{TableParameter}"));
                }
            }
        }

        return result;
    }

    private static void Walk(JsonNode node, string path, string nodeId, List<Reference> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    var childPath = $"{path}/{name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}";
                    if (child is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (name is ServerParameter or CredentialParameter && Guid.TryParse(text, out var id))
                        {
                            result.Add(new Reference(name, id, nodeId, childPath));
                        }
                    }
                    else if (child is not null)
                    {
                        Walk(child, childPath, nodeId, result);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is { } child)
                    {
                        Walk(child, $"{path}/{i}", nodeId, result);
                    }
                }

                break;
        }
    }

    private sealed record Reference(string Name, Guid Id, string NodeId, string Path);
}
