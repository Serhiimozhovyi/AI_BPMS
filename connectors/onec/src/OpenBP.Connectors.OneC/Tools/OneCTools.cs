using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace OpenBP.Connectors.OneC.Tools;

/// <summary>
/// Tools 1С для сценаріїв PRD №1–2, описані вручну поверх OData (<c>standard.odata</c>, OData v3): об'єкти й реквізити — з
/// <see cref="OneCOptions"/>. Описи — англійською (їх читають ШІ-агенти). <see cref="ConnectorException"/> → <c>isError</c>.
/// </summary>
/// <param name="odata">Клієнт OData-публікації.</param>
/// <param name="options">Налаштування.</param>
[McpServerToolType]
public sealed class OneCTools(RestClient odata, IOptions<ConnectorOptions> options)
{
    /// <summary>Скільки ключів контрагентів шукати одним запитом (довжина URL).</summary>
    private const int KeysPerRequest = 20;

    private const string EmptyKey = "00000000-0000-0000-0000-000000000000";

    private OneCOptions OneC => options.Value.OneC;

    /// <summary>
    /// <c>create_order</c>: контрагент за назвою, номенклатура за артикулом, запис документа замовлення (без проведення).
    /// </summary>
    /// <param name="customer">Назва контрагента.</param>
    /// <param name="lines">Рядки.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_order", Title = "Create customer order", UseStructuredContent = true, Destructive = false, OpenWorld = true)]
    [Description("Creates (writes, does not post) a customer order document in 1C: finds the customer by exact name and each item by SKU. Returns the order number.")]
    public Task<CreatedOrder> CreateOrder(
        [Description("Customer name exactly as in the 1C catalog, e.g. ТОВ Агро.")] string customer,
        [Description("Order lines: item SKU and quantity.")] OrderLine[] lines,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            if (string.IsNullOrWhiteSpace(customer))
            {
                throw new ConnectorException("customer — назва контрагента.");
            }

            if (lines is not { Length: > 0 })
            {
                throw new ConnectorException("lines — хоча б один рядок.");
            }

            if (lines.FirstOrDefault(l => string.IsNullOrWhiteSpace(l.Sku) || l.Qty <= 0) is { } bad)
            {
                throw new ConnectorException($"Рядок «{bad.Sku}»: потрібні sku і qty > 0.");
            }

            var (customerKey, customerName) = await FindCustomerAsync(customer.Trim(), ct).ConfigureAwait(false);
            var items = await FindItemsAsync(lines.Select(l => l.Sku.Trim()).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);

            var body = new Dictionary<string, object?>
            {
                ["Date"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                [OneC.OrderCustomerField] = customerKey,
                [OneC.OrderLinesField] = lines.Select((line, i) => new Dictionary<string, object?>
                {
                    ["LineNumber"] = (i + 1).ToString(CultureInfo.InvariantCulture),
                    [OneC.LineItemField] = items[line.Sku.Trim()],
                    [OneC.LineQuantityField] = line.Qty,
                }).ToList(),
            };
            var created = await odata.PostJsonAsync(Path(OneC.OrderDocument, []), body, ct).ConfigureAwait(false);
            return new CreatedOrder(
                String(created, "Number")?.Trim() ?? throw new ConnectorException("1С не повернула номер документа."),
                String(created, "Ref_Key"),
                String(created, "Date"),
                customerName,
                lines.Length);
        });

    /// <summary><c>sales_report</c>: проведені документи продажу за період з назвами контрагентів.</summary>
    /// <param name="from">Перший день.</param>
    /// <param name="to">Останній день.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "sales_report", Title = "Sales report", ReadOnly = true, UseStructuredContent = true, OpenWorld = true)]
    [Description("Returns posted sales documents for a period (both days inclusive) as { result: [{ number, date, customer, customer_key, amount }] }, ordered by date.")]
    public Task<ListResult<Sale>> SalesReport(
        [Description("First day, YYYY-MM-DD.")] string from,
        [Description("Last day, YYYY-MM-DD (inclusive).")] string to,
        CancellationToken ct = default) =>
        Call(async () =>
        {
            var first = Day(from, "from");
            var last = Day(to, "to");
            if (first > last)
            {
                throw new ConnectorException("from пізніше за to.");
            }

            var filter = $"Date ge datetime'{first:yyyy-MM-dd}T00:00:00' and Date le datetime'{last:yyyy-MM-dd}T23:59:59' " +
                         "and Posted eq true and DeletionMark eq false";
            var sales = Value(await odata.GetJsonAsync(
                Path(OneC.SalesDocument, [("$filter", filter), ("$select", $"Ref_Key,Number,Date,{OneC.SalesCustomerField},{OneC.SalesAmountField}"), ("$orderby", "Date")]),
                ct).ConfigureAwait(false));

            var keys = sales.Select(s => String(s, OneC.SalesCustomerField)).OfType<string>().Where(k => k != EmptyKey).Distinct().ToList();
            var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in keys.Chunk(KeysPerRequest))
            {
                var customers = await odata.GetJsonAsync(
                    Path(OneC.CustomerCatalog, [("$filter", string.Join(" or ", chunk.Select(k => $"Ref_Key eq guid'{Guid(k)}'"))), ("$select", "Ref_Key,Description")]),
                    ct).ConfigureAwait(false);
                foreach (var c in Value(customers))
                {
                    names[String(c, "Ref_Key") ?? ""] = String(c, "Description");
                }
            }

            return new ListResult<Sale>(sales.Select(s =>
            {
                var key = String(s, OneC.SalesCustomerField);
                return new Sale(
                    String(s, "Number")?.Trim(),
                    String(s, "Date"),
                    key is null ? null : names.GetValueOrDefault(key),
                    key,
                    s.TryGetProperty(OneC.SalesAmountField, out var amount) && amount.ValueKind == JsonValueKind.Number ? amount.GetDecimal() : 0);
            }).ToList());
        });

    private async Task<(string Key, string Name)> FindCustomerAsync(string name, CancellationToken ct)
    {
        var found = Value(await odata.GetJsonAsync(
            Path(OneC.CustomerCatalog, [("$filter", $"Description eq {Literal(name)} and DeletionMark eq false"), ("$select", "Ref_Key,Description"), ("$top", "2")]),
            ct).ConfigureAwait(false));
        return found.Count switch
        {
            0 => throw new ConnectorException($"Контрагента «{name}» не знайдено в {OneC.CustomerCatalog}."),
            > 1 => throw new ConnectorException($"Контрагентів з назвою «{name}» кілька — уточніть назву в 1С."),
            _ => (String(found[0], "Ref_Key")!, String(found[0], "Description") ?? name),
        };
    }

    /// <summary>Ключі номенклатури за артикулами одним запитом; кожен артикул має знайтися рівно раз.</summary>
    private async Task<Dictionary<string, string>> FindItemsAsync(IReadOnlyList<string> skus, CancellationToken ct)
    {
        var field = OneC.SkuField;
        var filter = $"({string.Join(" or ", skus.Select(s => $"{field} eq {Literal(s)}"))}) and DeletionMark eq false";
        var found = Value(await odata.GetJsonAsync(Path(OneC.ItemCatalog, [("$filter", filter), ("$select", $"Ref_Key,Description,{field}")]), ct).ConfigureAwait(false));
        var bySku = found.GroupBy(i => String(i, field)?.Trim() ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var missing = skus.Where(s => !bySku.ContainsKey(s)).ToList();
        if (missing.Count > 0)
        {
            throw new ConnectorException($"Номенклатуру з артикулом {string.Join(", ", missing.Select(s => $"«{s}»"))} не знайдено в {OneC.ItemCatalog}.");
        }

        if (skus.FirstOrDefault(s => bySku[s].Count > 1) is { } duplicate)
        {
            throw new ConnectorException($"Артикул «{duplicate}» мають кілька позицій номенклатури — уточніть у 1С.");
        }

        return skus.ToDictionary(s => s, s => String(bySku[s][0], "Ref_Key")!, StringComparer.Ordinal);
    }

    /// <summary>Шлях entity set із системними параметрами (і <c>$format=json</c>, якщо ввімкнено).</summary>
    private string Path(string entitySet, IReadOnlyList<(string Name, string Value)> query)
    {
        var parts = query.Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value)}").ToList();
        if (options.Value.OData.FormatParameter)
        {
            parts.Add("$format=json");
        }

        return Uri.EscapeDataString(entitySet) + (parts.Count > 0 ? "?" + string.Join('&', parts) : "");
    }

    /// <summary>Рядковий літерал OData: в лапках, <c>'</c> подвоюється.</summary>
    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Guid(string key) =>
        System.Guid.TryParse(key, out var guid) ? guid.ToString() : throw new ConnectorException($"1С повернула некоректний ключ «{key}».");

    private static DateOnly Day(string value, string name) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new ConnectorException($"{name} — дата у форматі YYYY-MM-DD.");

    private static List<JsonElement> Value(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : throw new ConnectorException("Відповідь OData без масиву value.");

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Помилку конектора віддає агенту текстом (<see cref="McpException"/> → <c>isError</c>).</summary>
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
