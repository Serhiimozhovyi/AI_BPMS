using System.Text.Json.Nodes;
using OpenBP.Engine.Data;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli;

/// <summary>
/// Таблиці даних для <c>openbp run</c>: у пам'яті, порожні, без БД. Таблицю на кожну назву з вузлів
/// <c>core.dataTable</c> сценарію створюємо самі — колонки (рядкові) беремо з полів <c>values</c> цих вузлів.
/// </summary>
internal static class CliDataTables
{
    public static InMemoryDataTableStore Create(WorkflowDefinition workflow)
    {
        var store = new InMemoryDataTableStore();
        var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in workflow.Nodes.Where(n => n.Type == "core.dataTable"))
        {
            if (node.Parameters["table"] is not JsonValue { } table || !table.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!columns.TryGetValue(name, out var list))
            {
                columns[name] = list = [];
            }

            if (node.Parameters["values"] is JsonObject values)
            {
                list.AddRange(values.Select(v => v.Key).Where(k => k != "_key" && !list.Contains(k)));
            }
        }

        foreach (var (name, list) in columns)
        {
            store.AddTable(name, [.. list.Distinct().Select(c => new DataColumn(c, DataColumnType.String))]);
        }

        return store;
    }
}
