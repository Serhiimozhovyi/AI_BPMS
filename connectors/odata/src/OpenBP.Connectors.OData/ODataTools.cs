using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.OData;

/// <summary>Колекція як <c>{ "result": [...] }</c>: <c>structuredContent</c> має бути об'єктом.</summary>
/// <typeparam name="T">Елемент.</typeparam>
/// <param name="Result">Елементи.</param>
public sealed record ListResult<T>([property: JsonPropertyName("result")] IReadOnlyList<T> Result);

/// <summary>Сторінка записів <c>query_entities</c>.</summary>
/// <param name="Result">Записи (без службових анотацій <c>@odata.*</c>).</param>
/// <param name="Count">Скільки записів повернуто.</param>
/// <param name="HasMore">Сервіс має ще записи понад <c>top</c>.</param>
public sealed record QueryResult(
    [property: JsonPropertyName("result")] IReadOnlyList<JsonElement> Result,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("hasMore")] bool HasMore);

/// <summary>Один запис.</summary>
/// <param name="Result">Запис (без службових анотацій <c>@odata.*</c>); для порожньої відповіді — <c>null</c>.</param>
public sealed record EntityResult([property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Result);

/// <summary>Результат видалення.</summary>
/// <param name="Deleted">Завжди <c>true</c>: помилка приходить як <c>isError</c>.</param>
public sealed record DeleteResult([property: JsonPropertyName("deleted")] bool Deleted);

/// <summary>
/// Tools довільного сервісу OData v4: набори сутностей з <c>$metadata</c>, запити з <c>$filter</c>/<c>$select</c>/<c>$expand</c>/
/// <c>$orderby</c> і сторінками, читання за ключем, створення, зміна (PATCH) і видалення. Описи — англійською (їх читають
/// ШІ-агенти). <see cref="ConnectorException"/> → <c>isError</c>.
/// </summary>
/// <param name="odata">Клієнт OData.</param>
/// <param name="metadata">Кеш <c>$metadata</c>.</param>
/// <param name="options">Налаштування.</param>
[McpServerToolType]
public sealed partial class ODataTools(ODataClient odata, MetadataCache metadata, IOptions<ConnectorOptions> options)
{
    private const int MaxPages = 50;

    /// <summary><c>list_entity_sets</c>.</summary>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_entity_sets", Title = "List entity sets", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Lists the entity sets of the OData service from $metadata: name, key properties, properties with EDM types and navigation properties (usable in $expand). Call it first to learn what can be queried.")]
    public Task<ListResult<EntitySetInfo>> ListEntitySets(CancellationToken ct = default) =>
        Call(async () => new ListResult<EntitySetInfo>(await metadata.GetAsync(ct).ConfigureAwait(false)));

    /// <summary><c>query_entities</c>.</summary>
    /// <param name="entitySet">Набір.</param>
    /// <param name="filter">$filter.</param>
    /// <param name="select">$select.</param>
    /// <param name="expand">$expand.</param>
    /// <param name="orderby">$orderby.</param>
    /// <param name="top">Скільки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "query_entities", Title = "Get records", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Reads records of an entity set with OData query options. Follows server paging (@odata.nextLink) until `top` records are collected; hasMore tells whether the service has more. Example filter: \"Status eq 'Open' and Amount gt 100\".")]
    public Task<QueryResult> QueryEntities(
        [Description("Entity set name from list_entity_sets, e.g. Orders.")] string entitySet,
        [Description("OData $filter expression, e.g. \"Name eq 'Acme'\" or \"contains(Name,'ac')\".")] string? filter = null,
        [Description("Comma-separated property names for $select, e.g. \"Id,Name\".")] string? select = null,
        [Description("Navigation properties for $expand, e.g. \"Lines\".")] string? expand = null,
        [Description("$orderby expression, e.g. \"Created desc\".")] string? orderby = null,
        [Description("Maximum number of records to return (default 20, capped by the connector's MaxTop).")] int top = 20,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            await metadata.RequireAsync(entitySet, ct).ConfigureAwait(false);
            var limit = Math.Clamp(top, 1, options.Value.MaxTop);
            var query = new List<string> { $"$top={limit}" };
            AddOption(query, "$filter", filter);
            AddOption(query, "$select", select);
            AddOption(query, "$expand", expand);
            AddOption(query, "$orderby", orderby);

            var rows = new List<JsonElement>();
            var hasMore = false;
            var path = $"{entitySet.Trim()}?{string.Join('&', query)}";
            for (var page = 0; page < MaxPages; page++)
            {
                var body = await odata.GetAsync(path, ct).ConfigureAwait(false);
                if (body is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                {
                    throw new ConnectorException("Сервіс повернув відповідь без масиву value: це не колекція OData v4.");
                }

                foreach (var row in value.EnumerateArray())
                {
                    if (rows.Count == limit)
                    {
                        hasMore = true;
                        break;
                    }

                    rows.Add(Clean(row));
                }

                if (hasMore)
                {
                    break;
                }

                var next = root.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
                if (next is null)
                {
                    break;
                }

                if (rows.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                path = next;
            }

            return new QueryResult(rows, rows.Count, hasMore);
        });

    /// <summary><c>get_entity</c>.</summary>
    /// <param name="entitySet">Набір.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="select">$select.</param>
    /// <param name="expand">$expand.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_entity", Title = "Get record by key", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns one record of an entity set by its key. Guids and numbers are used as they are, other text is quoted; a composite key can be passed as \"A=1,B='x'\".")]
    public Task<EntityResult> GetEntity(
        [Description("Entity set name from list_entity_sets.")] string entitySet,
        [Description("Record key, e.g. 5, 3f2c…-guid or ABC-1.")] string key,
        [Description("Comma-separated property names for $select.")] string? select = null,
        [Description("Navigation properties for $expand.")] string? expand = null,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            await metadata.RequireAsync(entitySet, ct).ConfigureAwait(false);
            var query = new List<string>();
            AddOption(query, "$select", select);
            AddOption(query, "$expand", expand);
            var body = await odata.GetAsync(RecordPath(entitySet, key, query), ct, notFoundIsNull: true).ConfigureAwait(false);
            return body is { } record
                ? new EntityResult(Clean(record))
                : throw new ConnectorException($"Запису з ключем {key} у наборі «{entitySet}» немає (404).");
        });

    /// <summary><c>create_entity</c>.</summary>
    /// <param name="entitySet">Набір.</param>
    /// <param name="data">Поля.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_entity", Title = "Create record", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Creates a record in an entity set (POST). Pass the properties as an object, e.g. { \"Name\": \"Acme\", \"Amount\": 100 }. Returns the created record when the service sends it back.")]
    public Task<EntityResult> CreateEntity(
        [Description("Entity set name from list_entity_sets.")] string entitySet,
        [Description("Properties of the new record.")] Dictionary<string, JsonElement> data,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            await metadata.RequireAsync(entitySet, ct).ConfigureAwait(false);
            RequireData(data);
            var body = await odata.SendAsync(HttpMethod.Post, entitySet.Trim(), data, ct).ConfigureAwait(false);
            return new EntityResult(body is { } record ? Clean(record) : null);
        });

    /// <summary><c>update_entity</c>.</summary>
    /// <param name="entitySet">Набір.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="data">Поля.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "update_entity", Title = "Update record", UseStructuredContent = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Changes properties of a record (PATCH): pass only the properties to change. Returns the record after the change when the service sends it back.")]
    public Task<EntityResult> UpdateEntity(
        [Description("Entity set name from list_entity_sets.")] string entitySet,
        [Description("Record key.")] string key,
        [Description("Properties to change, e.g. { \"Status\": \"Closed\" }.")] Dictionary<string, JsonElement> data,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            await metadata.RequireAsync(entitySet, ct).ConfigureAwait(false);
            RequireData(data);
            var body = await odata.SendAsync(HttpMethod.Patch, RecordPath(entitySet, key, []), data, ct).ConfigureAwait(false);
            return new EntityResult(body is { } record ? Clean(record) : null);
        });

    /// <summary><c>delete_entity</c>.</summary>
    /// <param name="entitySet">Набір.</param>
    /// <param name="key">Ключ.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "delete_entity", Title = "Delete record", UseStructuredContent = true, Destructive = true, Idempotent = true, OpenWorld = true)]
    [Description("Deletes a record by key (DELETE). Cannot be undone.")]
    public Task<DeleteResult> DeleteEntity(
        [Description("Entity set name from list_entity_sets.")] string entitySet,
        [Description("Record key.")] string key,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            await metadata.RequireAsync(entitySet, ct).ConfigureAwait(false);
            await odata.SendAsync(HttpMethod.Delete, RecordPath(entitySet, key, []), null, ct).ConfigureAwait(false);
            return new DeleteResult(true);
        });

    /// <summary>
    /// Літерал ключа для шляху <c>Set(key)</c>: число, Guid і вже взятий у лапки рядок — як є, складений ключ
    /// (<c>A=1,B='x'</c>) — як є, решта — рядок у лапках з подвоєними апострофами.
    /// </summary>
    /// <param name="key">Ключ від користувача.</param>
    /// <exception cref="ConnectorException">Порожній ключ.</exception>
    public static string KeyLiteral(string key)
    {
        var text = key?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ConnectorException("Вкажіть ключ запису (key).");
        }

        if (NumberKey().IsMatch(text) || Guid.TryParse(text, out _) || text.Contains('=') || (text.Length >= 2 && text[0] == '\'' && text[^1] == '\''))
        {
            return text;
        }

        return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex NumberKey();

    private static string RecordPath(string entitySet, string key, List<string> query)
    {
        // Апострофи, «=» і коми лишаються читабельними; решта зарезервованих символів у ключі екранується.
        var literal = Uri.EscapeDataString(KeyLiteral(key)).Replace("%27", "'", StringComparison.Ordinal).Replace("%3D", "=", StringComparison.Ordinal).Replace("%2C", ",", StringComparison.Ordinal);
        return $"{entitySet.Trim()}({literal})" + (query.Count > 0 ? "?" + string.Join('&', query) : "");
    }

    private static void AddOption(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
        }
    }

    private static void RequireData(Dictionary<string, JsonElement>? data)
    {
        if (data is not { Count: > 0 })
        {
            throw new ConnectorException("Передайте хоча б одну властивість у data.");
        }
    }

    /// <summary>Прибирає службові анотації <c>@odata.*</c> з запису.</summary>
    private static JsonElement Clean(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || JsonNode.Parse(record.GetRawText()) is not JsonObject obj)
        {
            return record;
        }

        foreach (var name in obj.Select(p => p.Key).Where(k => k.StartsWith("@odata.", StringComparison.Ordinal)).ToList())
        {
            obj.Remove(name);
        }

        return JsonSerializer.SerializeToElement(obj);
    }

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ConnectorException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
