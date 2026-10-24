using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Engine.Data;

namespace OpenBP.Application.DataTables;

/// <summary>Таблиця даних у списку і деталях (<c>GET /tables</c>).</summary>
/// <param name="Id"><c>data_tables.id</c>.</param>
/// <param name="Name">Назва, унікальна без урахування регістру.</param>
/// <param name="Columns">Колонки.</param>
/// <param name="RowCount">Скільки рядків.</param>
/// <param name="UpdatedAt">Остання зміна структури.</param>
public sealed record DataTableSummary(Guid Id, string Name, IReadOnlyList<DataColumn> Columns, int RowCount, DateTimeOffset UpdatedAt);

/// <summary>Рядок таблиці для REST і UI.</summary>
/// <param name="Key">Ключ.</param>
/// <param name="Data">Значення колонок.</param>
/// <param name="CreatedAt">Створено.</param>
/// <param name="UpdatedAt">Змінено.</param>
public sealed record DataRowDto(string Key, JsonObject Data, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Результат імпорту CSV.</summary>
/// <param name="Inserted">Нових рядків.</param>
/// <param name="Updated">Оновлених за ключем.</param>
public sealed record DataImportResult(int Inserted, int Updated);

/// <summary>
/// Таблиці даних (F6-11): структура і рядки для REST, UI і MCP. Ролі: читання — <see cref="UserRole.Viewer"/>,
/// зміна таблиць і рядків — <see cref="UserRole.Editor"/>. Правила значень — ті самі, що у вузла <c>core.dataTable</c>.
/// </summary>
public interface IDataTableService
{
    /// <summary>Усі таблиці за назвою.</summary>
    Task<IReadOnlyList<DataTableSummary>> ListAsync(CancellationToken ct);

    /// <summary>Таблиця за id або назвою.</summary>
    /// <exception cref="NotFoundException">Таблиці немає.</exception>
    Task<DataTableSummary> GetAsync(string reference, CancellationToken ct);

    /// <summary>Нова таблиця.</summary>
    /// <exception cref="ConflictException">Назва зайнята (<c>DATA_TABLE_NAME_TAKEN</c>).</exception>
    Task<DataTableSummary> CreateAsync(string name, IReadOnlyList<DataColumn> columns, CancellationToken ct);

    /// <summary>
    /// Перейменування і зміна колонок. Значення видаленої колонки прибираються з рядків; ключову колонку таблиці з рядками
    /// змінити не можна (<c>DATA_TABLE_KEY_CHANGE</c>).
    /// </summary>
    Task<DataTableSummary> UpdateAsync(Guid id, string name, IReadOnlyList<DataColumn> columns, CancellationToken ct);

    /// <summary>Видаляє таблицю з рядками.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Рядки в порядку створення; <paramref name="search"/> — підрядок будь-якого значення (без урахування регістру).</summary>
    Task<PageResult<DataRowDto>> ListRowsAsync(Guid id, string? search, PageRequest page, CancellationToken ct);

    /// <summary>Рядки за умовами (MCP <c>query_table</c>).</summary>
    Task<IReadOnlyList<DataRowDto>> QueryRowsAsync(string reference, IReadOnlyList<DataFilter> where, int limit, CancellationToken ct);

    /// <summary>Новий рядок.</summary>
    Task<DataRowDto> InsertRowAsync(Guid id, JsonObject values, CancellationToken ct);

    /// <summary>Дописує значення в рядок.</summary>
    /// <exception cref="NotFoundException">Рядка немає.</exception>
    Task<DataRowDto> UpdateRowAsync(Guid id, string key, JsonObject values, CancellationToken ct);

    /// <summary>Видаляє рядок.</summary>
    /// <exception cref="NotFoundException">Рядка немає.</exception>
    Task DeleteRowAsync(Guid id, string key, CancellationToken ct);

    /// <summary>
    /// Імпорт CSV: перший рядок — назви колонок (усі мають бути в таблиці), роздільник <c>,</c> або <c>;</c>. З ключовою
    /// колонкою — upsert за ключем, без неї — нові рядки. Усі рядки перевіряються до запису; помилка — з номером рядка CSV.
    /// </summary>
    Task<DataImportResult> ImportCsvAsync(Guid id, string csv, CancellationToken ct);
}

/// <summary>Помилка даних таблиці для REST: код з <see cref="DataTableRules.Codes"/>, 422 (дубль ключа — 409).</summary>
public sealed class DataTableValidationException : OpenBpException
{
    /// <summary>Обгортає помилку правил.</summary>
    /// <param name="inner">Помилка <see cref="DataTableRules"/>.</param>
    /// <param name="prefix">Уточнення, напр. номер рядка CSV.</param>
    public DataTableValidationException(DataTableException inner, string? prefix = null)
        : base(
            inner?.Code ?? DataTableRules.Codes.ColumnsInvalid,
            inner?.Code == DataTableRules.Codes.KeyDuplicate ? 409 : 422,
            prefix is null ? inner?.Message ?? "" : $"{prefix}: {inner?.Message}",
            inner)
    {
    }
}
