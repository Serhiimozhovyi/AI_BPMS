using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Data;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.dataTable</c> (F6-11) — читання й запис рядків таблиці даних OpenBP. Операція виконується для кожного
/// вхідного item'а: <c>get</c> — item з рядком (нічого, якщо рядка немає), <c>list</c> — по item'у на рядок за умовами
/// <c>where</c> (до <c>limit</c>), <c>insert</c>/<c>upsert</c>/<c>update</c> — item із записаним рядком,
/// <c>delete</c> — <c>{ _key, deleted }</c>. У <c>json</c> рядка є <c>_key</c> — ключ, за яким його можна змінити чи
/// видалити. Типи, ліміти й дублі ключа перевіряє <see cref="IDataTableStore"/>; їхні помилки — помилка вузла.
/// <para>
/// F8-02 (додано до версії 1, старі сценарії працюють як раніше): <c>insertIf</c> — вставка, лише коли рядків за
/// <c>where</c> менше ніж <c>maxCount</c>; <c>increment</c> — атомарно додає <c>by</c> до числової колонки;
/// <c>count</c> — <c>{ count }</c>. Порушення правила унікальності таблиці в <c>insert</c>/<c>upsert</c>/<c>insertIf</c> і
/// брак місця в <c>insertIf</c> — не помилка, а item <c>{ _inserted: false, _reason: "unique" | "capacity" }</c>, щоб
/// сценарій міг відповісти людині; успішний <c>insertIf</c> дає рядок з <c>_inserted: true</c>. Таблицю шукає
/// <see cref="IDataTableStore.FindTableAsync(string, Guid, CancellationToken)"/>: проєкт сценарію, потім спільні.
/// </para>
/// </summary>
public sealed class DataTableNode(IDataTableStore? store) : INode
{
    /// <summary>Поле item'а з ключем рядка.</summary>
    public const string KeyField = "_key";

    /// <summary>Поле item'а: чи вставлено рядок (F8-02).</summary>
    public const string InsertedField = "_inserted";

    /// <summary>Поле item'а: чому рядок не вставлено (<see cref="DataInsertReasons"/>).</summary>
    public const string ReasonField = "_reason";

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.dataTable",
        Version: 1,
        DisplayName: "Таблиця даних",
        Group: NodeGroup.Transform,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("dataTable"),
        Description: "Читає й записує рядки таблиці даних OpenBP: get, list, count, insert, insertIf, upsert, update, increment, delete.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var tables = store ?? throw new CoreNodeException("Таблиці даних недоступні: сервер не налаштував сховище. Зверніться до адміністратора.");
        var items = ctx.Items();
        var output = new List<Item>();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<DataTableParameters>(i);
            var table = await tables.FindTableAsync(p.Table, ctx.ExecutionId, ct).ConfigureAwait(false)
                ?? throw new CoreNodeException($"Таблиці «{p.Table}» немає. Оберіть наявну таблицю або створіть її в розділі «Таблиці».");
            try
            {
                output.AddRange(await RunAsync(tables, table, p, ct).ConfigureAwait(false));
            }
            catch (DataTableException ex)
            {
                throw new CoreNodeException($"{ex.Message.TrimEnd('.')}. Перевірте параметри вузла і дані item'а.", ex).AtItem(i, items.Count);
            }
        }

        return [[.. output]];
    }

    private static async Task<IEnumerable<Item>> RunAsync(IDataTableStore tables, DataTableInfo table, DataTableParameters p, CancellationToken ct)
    {
        switch (p.Operation)
        {
            case "get":
                var found = await tables.GetRowAsync(table, Key(p), ct).ConfigureAwait(false);
                return found is null ? [] : [ToItem(found)];
            case "list":
                var rows = await tables.QueryRowsAsync(table, Filters(p), p.Limit, 0, ct).ConfigureAwait(false);
                return rows.Select(ToItem);
            case "count":
                var count = await tables.CountRowsAsync(table, Filters(p), ct).ConfigureAwait(false);
                return [new Item { Json = new JsonObject { ["count"] = count } }];
            case "insert":
                return [await RefusedOnUniqueAsync(() => tables.InsertRowAsync(table, Values(p), ct)).ConfigureAwait(false)];
            case "upsert":
                return [await RefusedOnUniqueAsync(() => tables.UpsertRowAsync(table, Values(p), ct)).ConfigureAwait(false)];
            case "insertIf":
                var maxCount = p.MaxCount ?? throw new CoreNodeException("Для операції insertIf не задано maxCount — найбільшу кількість рядків за умовою where.");
                var result = await tables.InsertIfAsync(table, Values(p), Filters(p), maxCount, ct).ConfigureAwait(false);
                if (!result.Inserted)
                {
                    return [Refused(result.Reason ?? DataInsertReasons.Unique)];
                }

                var inserted = ToItem(result.Row!);
                inserted.Json[InsertedField] = true;
                return [inserted];
            case "increment":
                var incrementKey = Key(p);
                var column = string.IsNullOrWhiteSpace(p.Column)
                    ? throw new CoreNodeException("Для операції increment не задано колонку (column) — числову колонку таблиці.")
                    : p.Column;
                var incremented = await tables.IncrementAsync(table, incrementKey, column, p.By ?? 1m, ct).ConfigureAwait(false)
                    ?? throw new CoreNodeException($"У таблиці «{table.Name}» немає рядка з ключем «{incrementKey}». Перевірте ключ.");
                return [ToItem(incremented)];
            case "update":
                var key = Key(p);
                var updated = await tables.UpdateRowAsync(table, key, Values(p), ct).ConfigureAwait(false)
                    ?? throw new CoreNodeException($"У таблиці «{table.Name}» немає рядка з ключем «{key}». Перевірте ключ або оберіть операцію upsert.");
                return [ToItem(updated)];
            case "delete":
                var deleteKey = Key(p);
                var deleted = await tables.DeleteRowAsync(table, deleteKey, ct).ConfigureAwait(false);
                return [new Item { Json = new JsonObject { [KeyField] = deleteKey, ["deleted"] = deleted } }];
            default:
                throw new CoreNodeException($"Невідома операція «{p.Operation}». Оберіть get, list, count, insert, insertIf, upsert, update, increment або delete.");
        }
    }

    private static async Task<Item> RefusedOnUniqueAsync(Func<Task<DataRow>> write)
    {
        try
        {
            return ToItem(await write().ConfigureAwait(false));
        }
        catch (DataTableException ex) when (ex.Code == DataTableRules.Codes.Unique)
        {
            return Refused(DataInsertReasons.Unique);
        }
    }

    private static Item Refused(string reason) => new() { Json = new JsonObject { [InsertedField] = false, [ReasonField] = reason } };

    private static Item ToItem(DataRow row)
    {
        var json = (JsonObject)row.Data.DeepClone();
        json[KeyField] = row.Key;
        return new Item { Json = json };
    }

    private static string Key(DataTableParameters p) => p.Key switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => DataTableRules.KeyText(v)!,
        _ => throw new CoreNodeException($"Для операції {p.Operation} не задано ключ (key). Вкажіть значення або вираз."),
    };

    private static JsonObject Values(DataTableParameters p)
    {
        var values = p.Values as JsonObject ?? throw new CoreNodeException($"Для операції {p.Operation} не задано значення (values). Додайте поля рядка.");
        // Службові поля з виходу попереднього вузла не є колонками.
        var copy = (JsonObject)values.DeepClone();
        copy.Remove(KeyField);
        copy.Remove(InsertedField);
        copy.Remove(ReasonField);
        return copy;
    }

    private static List<DataFilter> Filters(DataTableParameters p) =>
        [.. p.Where.Select(w => new DataFilter(w.Column, w.Operator, w.Value?.DeepClone()))];

    private sealed class DataTableParameters
    {
        public string Table { get; set; } = "";

        public string Operation { get; set; } = "";

        public JsonNode? Key { get; set; }

        public List<FilterParameter> Where { get; set; } = [];

        public JsonNode? Values { get; set; }

        public int Limit { get; set; } = 100;

        public int? MaxCount { get; set; }

        public string? Column { get; set; }

        public decimal? By { get; set; }
    }

    private sealed class FilterParameter
    {
        public string Column { get; set; } = "";

        public string Operator { get; set; } = "";

        public JsonNode? Value { get; set; }
    }
}
