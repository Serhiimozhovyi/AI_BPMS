using System.Text.Json.Nodes;
using OpenBP.Engine.Data;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Cli;

/// <summary>
/// Таблиці даних для <c>openbp run</c>: у пам'яті, порожні, без БД. Таблицю на кожну назву з вузлів
/// <c>core.dataTable</c> сценарію створюємо самі — колонки беремо з полів <c>values</c>, умов <c>where</c> і колонки
/// <c>increment</c> цих вузлів (F8-02): колонка <c>increment</c> числова, решта — рядкові. Обмежень і проєктів немає.
/// Таблиця, з якої лише читають без умов, не створюється (F8-08): її вузли мають бути закріплені pinData.
/// </summary>
internal static class CliDataTables
{
    public static InMemoryDataTableStore Create(WorkflowDefinition workflow)
    {
        var store = new InMemoryDataTableStore();
        var columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] service = ["_key", "_inserted", "_reason"];
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
                list.AddRange(values.Select(v => v.Key).Where(k => !service.Contains(k) && !list.Contains(k)));
            }

            if (node.Parameters["where"] is JsonArray where)
            {
                list.AddRange(where.Select(w => (w as JsonObject)?["column"] is JsonValue c && c.TryGetValue<string>(out var n) ? n : null)
                    .OfType<string>().Where(n => !list.Contains(n)));
            }

            if (node.Parameters["column"] is JsonValue increment && increment.TryGetValue<string>(out var counter) && !string.IsNullOrWhiteSpace(counter))
            {
                numbers.Add(counter);
                if (!list.Contains(counter))
                {
                    list.Add(counter);
                }
            }
        }

        // Назва таблиці чи колонки з виразом визначається лише під час виконання — таку не вгадуємо. Таблиця лише з читанням
        // без умов (get, list) колонок не має: її не створюємо, а вузол без pinData скаже, що таблиці немає.
        foreach (var (name, list) in columns.Where(c => !ExpressionSyntax.ContainsExpression(c.Key)))
        {
            var valid = list.Where(DataTableRules.IsValidColumnName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (valid.Count > 0)
            {
                store.AddTable(name, [.. valid.Select(c => new DataColumn(c, numbers.Contains(c) ? DataColumnType.Number : DataColumnType.String))]);
            }
        }

        return store;
    }
}
