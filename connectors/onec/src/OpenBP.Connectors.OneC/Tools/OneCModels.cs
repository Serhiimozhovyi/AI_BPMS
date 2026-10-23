using System.ComponentModel;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.OneC.Tools;

/// <summary>Рядок <c>create_order</c>.</summary>
/// <param name="Sku">Артикул.</param>
/// <param name="Qty">Кількість.</param>
public sealed record OrderLine(
    [property: JsonPropertyName("sku"), Description("Item SKU (article) exactly as in 1C.")] string Sku,
    [property: JsonPropertyName("qty"), Description("Quantity, greater than 0.")] double Qty);

/// <summary>Результат <c>create_order</c>.</summary>
/// <param name="Number">Номер документа.</param>
/// <param name="RefKey">Ключ документа.</param>
/// <param name="Date">Дата документа.</param>
/// <param name="Customer">Назва знайденого контрагента.</param>
/// <param name="LineCount">Кількість рядків.</param>
public sealed record CreatedOrder(
    [property: JsonPropertyName("number")] string Number,
    [property: JsonPropertyName("ref_key")] string? RefKey,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("line_count")] int LineCount);

/// <summary>Рядок <c>sales_report</c>.</summary>
/// <param name="Number">Номер документа.</param>
/// <param name="Date">Дата.</param>
/// <param name="Customer">Назва контрагента.</param>
/// <param name="CustomerKey">Ключ контрагента.</param>
/// <param name="Amount">Сума документа.</param>
public sealed record Sale(
    [property: JsonPropertyName("number")] string? Number,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("customer")] string? Customer,
    [property: JsonPropertyName("customer_key")] string? CustomerKey,
    [property: JsonPropertyName("amount")] decimal Amount);

/// <summary>Колекція як <c>{ "result": [...] }</c>: <c>structuredContent</c> має бути об'єктом.</summary>
/// <typeparam name="T">Елемент.</typeparam>
/// <param name="Result">Елементи.</param>
public sealed record ListResult<T>([property: JsonPropertyName("result")] IReadOnlyList<T> Result);
