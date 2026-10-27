using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OpenBP.Connectors.Sql.Tools;

/// <summary>Таблиця в <c>list_tables</c>.</summary>
/// <param name="Table">Назва для аргументу <c>table</c> інших tools: без схеми, якщо таблиця в поточній схемі, інакше <c>схема.назва</c>.</param>
/// <param name="Schema">Схема (Postgres) чи база (MySQL).</param>
/// <param name="Name">Назва.</param>
/// <param name="Type"><c>table</c> або <c>view</c>.</param>
public sealed record TableItem(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);

/// <summary>Результат <c>list_tables</c>.</summary>
/// <param name="Result">Таблиці за схемою й назвою.</param>
public sealed record TablesResult([property: JsonPropertyName("result")] IReadOnlyList<TableItem> Result);

/// <summary>Колонка в <c>describe_table</c>.</summary>
/// <param name="Name">Назва.</param>
/// <param name="Type">Тип бази.</param>
/// <param name="Nullable">Чи допускає <c>null</c>.</param>
/// <param name="PrimaryKey">Чи входить у первинний ключ.</param>
/// <param name="Default">Значення за замовчуванням (вираз) або <c>null</c>.</param>
public sealed record ColumnItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("nullable")] bool Nullable,
    [property: JsonPropertyName("primaryKey")] bool PrimaryKey,
    [property: JsonPropertyName("default")] string? Default);

/// <summary>Результат <c>describe_table</c>.</summary>
/// <param name="Table">Назва для аргументу <c>table</c>.</param>
/// <param name="Schema">Схема.</param>
/// <param name="Name">Назва.</param>
/// <param name="Columns">Колонки в порядку оголошення.</param>
/// <param name="PrimaryKey">Колонки первинного ключа.</param>
public sealed record DescribeResult(
    [property: JsonPropertyName("table")] string Table,
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("columns")] IReadOnlyList<ColumnItem> Columns,
    [property: JsonPropertyName("primaryKey")] IReadOnlyList<string> PrimaryKey);

/// <summary>Колонка результату запиту.</summary>
/// <param name="Name">Назва (дублікати — з суфіксом <c>_2</c>).</param>
/// <param name="Type">Тип бази.</param>
public sealed record ResultColumn(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type);

/// <summary>Результат <c>query</c>.</summary>
/// <param name="Result">Рядки: об'єкт «колонка → значення».</param>
/// <param name="Columns">Колонки результату.</param>
/// <param name="RowCount">Кількість рядків у <paramref name="Result"/>.</param>
/// <param name="Truncated">Рядків було більше за ліміт.</param>
public sealed record QueryResult(
    [property: JsonPropertyName("result")] IReadOnlyList<JsonObject> Result,
    [property: JsonPropertyName("columns")] IReadOnlyList<ResultColumn> Columns,
    [property: JsonPropertyName("rowCount")] int RowCount,
    [property: JsonPropertyName("truncated")] bool Truncated);

/// <summary>Результат tools запису.</summary>
/// <param name="AffectedRows">Змінено рядків (MySQL у upsert рахує оновлений рядок за два).</param>
/// <param name="Result">Рядки після зміни (Postgres з <c>returning</c>, або <c>execute</c> з <c>RETURNING</c>/<c>SELECT</c>); інакше порожньо.</param>
/// <param name="LastInsertId">MySQL: id останнього вставленого рядка (<c>AUTO_INCREMENT</c>) або <c>null</c>.</param>
public sealed record WriteResult(
    [property: JsonPropertyName("affectedRows")] int AffectedRows,
    [property: JsonPropertyName("result")] IReadOnlyList<JsonObject> Result,
    [property: JsonPropertyName("lastInsertId")] long? LastInsertId);

/// <summary>Результат <c>changes_since</c> (docs/connectors.md §8).</summary>
/// <param name="Result">Рядки від старих до нових; у кожному <c>_changeKey</c> — ключ рядка і значення курсора.</param>
/// <param name="Cursor">Значення колонки курсора в останньому рядку (або вхідний <c>since</c>), для наступного виклику.</param>
/// <param name="Column">Колонка курсора.</param>
public sealed record ChangesResult(
    [property: JsonPropertyName("result")] IReadOnlyList<JsonObject> Result,
    [property: JsonPropertyName("cursor")] string? Cursor,
    [property: JsonPropertyName("column")] string Column);
