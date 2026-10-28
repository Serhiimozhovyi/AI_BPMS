using System.Text.Json.Nodes;

namespace OpenBP.Engine.Data;

/// <summary>Тип колонки таблиці даних (F6-11).</summary>
public enum DataColumnType
{
    /// <summary>Рядок.</summary>
    String = 0,

    /// <summary>Число.</summary>
    Number = 1,

    /// <summary><c>true</c>/<c>false</c>.</summary>
    Boolean = 2,

    /// <summary>Дата й час; зберігається рядком ISO 8601 в UTC, тож рядки порівнюються як дати.</summary>
    Date = 3,

    /// <summary>Будь-яке JSON-значення.</summary>
    Json = 4,

    /// <summary>Момент часу з поясом (F8-02); зберігається рядком ISO 8601 в UTC.</summary>
    Datetime = 5,

    /// <summary>Одне значення з переліку <see cref="DataColumn.Values"/> (F8-02).</summary>
    Enum = 6,

    /// <summary>Ключ рядка таблиці <see cref="DataColumn.Table"/> того самого проєкту або спільної (F8-02).</summary>
    Ref = 7,

    /// <summary>Id користувача OpenBP (F8-02).</summary>
    User = 8,
}

/// <summary>Колонка таблиці даних.</summary>
/// <param name="Name">Назва: латиниця, цифри, <c>_</c>, не з цифри.</param>
/// <param name="Type">Тип значень.</param>
/// <param name="Key">Ключова колонка: її значення — ключ рядка, унікальний у таблиці (не більше однієї на таблицю).</param>
public sealed record DataColumn(string Name, DataColumnType Type, bool Key = false)
{
    /// <summary>Допустимі значення колонки <see cref="DataColumnType.Enum"/>.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Назва таблиці, на рядки якої посилається колонка <see cref="DataColumnType.Ref"/>.</summary>
    public string? Table { get; init; }

    /// <summary>
    /// Чутливі дані (телефон, <c>telegram_id</c>): значення бачать лише editor проєкту й admin, читання пишеться в
    /// audit log (F8-02).
    /// </summary>
    public bool Sensitive { get; init; }
}

/// <summary>Часткова унікальність: значення <see cref="Columns"/> не повторюються серед рядків, що задовольняють <see cref="Where"/>.</summary>
/// <param name="Columns">Колонки, сукупність значень яких унікальна. Рядок з порожнім значенням будь-якої з них не перевіряється.</param>
/// <param name="Where">Умова в синтаксисі <c>where</c> вузла; <c>null</c> — усі рядки.</param>
public sealed record DataUniqueConstraint(IReadOnlyList<string> Columns, IReadOnlyList<DataFilter>? Where = null);

/// <summary>Обмеження цілісності таблиці (F8-02, <c>data_tables.constraints</c>).</summary>
public sealed record DataTableConstraints
{
    /// <summary>Без обмежень.</summary>
    public static DataTableConstraints None { get; } = new();

    /// <summary>Колонки, значення яких обов'язкові в кожному рядку.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];

    /// <summary>Правила унікальності.</summary>
    public IReadOnlyList<DataUniqueConstraint> Unique { get; init; } = [];

    /// <summary>Чи немає жодного обмеження.</summary>
    public bool IsEmpty => Required.Count == 0 && Unique.Count == 0;
}

/// <summary>Таблиця даних, як її бачать вузол <c>core.dataTable</c> і сервіс.</summary>
/// <param name="Id"><c>data_tables.id</c>.</param>
/// <param name="Name">Назва, унікальна без урахування регістру в межах проєкту.</param>
/// <param name="Columns">Колонки в порядку показу.</param>
public sealed record DataTableInfo(Guid Id, string Name, IReadOnlyList<DataColumn> Columns)
{
    /// <summary>Ключова колонка або <c>null</c> — тоді ключ рядка генерується.</summary>
    public DataColumn? KeyColumn => Columns.FirstOrDefault(c => c.Key);

    /// <summary>Проєкт таблиці; <c>null</c> — спільна (F8-02).</summary>
    public Guid? ProjectId { get; init; }

    /// <summary>Обмеження цілісності.</summary>
    public DataTableConstraints Constraints { get; init; } = DataTableConstraints.None;
}

/// <summary>Результат <see cref="IDataTableStore.InsertIfAsync"/>.</summary>
/// <param name="Inserted">Рядок вставлено.</param>
/// <param name="Reason">Чому ні: <see cref="DataInsertReasons"/>.</param>
/// <param name="Row">Вставлений рядок.</param>
public sealed record DataInsertResult(bool Inserted, string? Reason, DataRow? Row);

/// <summary>Причини відмови у вставці.</summary>
public static class DataInsertReasons
{
    /// <summary>Порушено унікальність (<see cref="DataTableConstraints.Unique"/> або ключ).</summary>
    public const string Unique = "unique";

    /// <summary>Рядків за умовою вже <c>maxCount</c>.</summary>
    public const string Capacity = "capacity";
}

/// <summary>Рядок таблиці даних.</summary>
/// <param name="Key">Ключ: значення ключової колонки або згенерований id.</param>
/// <param name="Data">Значення колонок.</param>
/// <param name="CreatedAt">Створено.</param>
/// <param name="UpdatedAt">Змінено.</param>
public sealed record DataRow(string Key, JsonObject Data, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Умова відбору рядків: колонка, оператор (<see cref="DataFilterOperators"/>), значення.</summary>
/// <param name="Column">Колонка.</param>
/// <param name="Operator">Оператор.</param>
/// <param name="Value">Значення для порівняння; для <c>isEmpty</c>/<c>isNotEmpty</c> не потрібне.</param>
public sealed record DataFilter(string Column, string Operator, JsonNode? Value = null);

/// <summary>Оператори <see cref="DataFilter"/>.</summary>
public static class DataFilterOperators
{
    /// <summary>Дорівнює.</summary>
    public const string Eq = "eq";

    /// <summary>Не дорівнює.</summary>
    public const string Ne = "ne";

    /// <summary>Більше.</summary>
    public const string Gt = "gt";

    /// <summary>Більше або дорівнює.</summary>
    public const string Gte = "gte";

    /// <summary>Менше.</summary>
    public const string Lt = "lt";

    /// <summary>Менше або дорівнює.</summary>
    public const string Lte = "lte";

    /// <summary>Рядок містить (без урахування регістру).</summary>
    public const string Contains = "contains";

    /// <summary>Значення немає або порожній рядок.</summary>
    public const string IsEmpty = "isEmpty";

    /// <summary>Значення є і не порожній рядок.</summary>
    public const string IsNotEmpty = "isNotEmpty";

    /// <summary>Усі оператори.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Eq, Ne, Gt, Gte, Lt, Lte, Contains, IsEmpty, IsNotEmpty,
    };
}

/// <summary>
/// Сховище рядків таблиць даних для вузла <c>core.dataTable</c> (F6-11). Хост дає реалізацію: воркер — над Postgres,
/// тести й CLI — у пам'яті. Перевірки типів і лімітів однакові для всіх (<see cref="DataTableRules"/>); помилки даних —
/// <see cref="DataTableException"/>.
/// </summary>
public interface IDataTableStore
{
    /// <summary>Таблиця за id або назвою (без урахування регістру); <c>null</c> — немає.</summary>
    /// <param name="reference">Id або назва.</param>
    /// <param name="ct">Скасування.</param>
    Task<DataTableInfo?> FindTableAsync(string reference, CancellationToken ct);

    /// <summary>
    /// Таблиця для вузла виконання <paramref name="executionId"/> (F8-02): за id — таблиця проєкту сценарію або спільна,
    /// за назвою — спершу в проєкті сценарію, потім спільна. Таблиці інших проєктів для сценарію не існує.
    /// Типово — як <see cref="FindTableAsync(string, CancellationToken)"/> (сховища без проєктів).
    /// </summary>
    /// <param name="reference">Id або назва.</param>
    /// <param name="executionId">Виконання, з якого звертається вузол.</param>
    /// <param name="ct">Скасування.</param>
    Task<DataTableInfo?> FindTableAsync(string reference, Guid executionId, CancellationToken ct) => FindTableAsync(reference, ct);

    /// <summary>Рядок за ключем; <c>null</c> — немає.</summary>
    Task<DataRow?> GetRowAsync(DataTableInfo table, string key, CancellationToken ct);

    /// <summary>Рядки, що задовольняють усі умови, у порядку створення.</summary>
    Task<IReadOnlyList<DataRow>> QueryRowsAsync(DataTableInfo table, IReadOnlyList<DataFilter> where, int limit, int offset, CancellationToken ct);

    /// <summary>Новий рядок; рядок з таким ключем уже є — <see cref="DataTableException"/>.</summary>
    Task<DataRow> InsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct);

    /// <summary>Створює рядок або дописує значення в наявний з тим самим ключем; потрібна ключова колонка.</summary>
    Task<DataRow> UpsertRowAsync(DataTableInfo table, JsonObject values, CancellationToken ct);

    /// <summary>Дописує значення в рядок; <c>null</c> — рядка немає.</summary>
    Task<DataRow?> UpdateRowAsync(DataTableInfo table, string key, JsonObject values, CancellationToken ct);

    /// <summary>Видаляє рядок; <c>false</c> — рядка не було.</summary>
    Task<bool> DeleteRowAsync(DataTableInfo table, string key, CancellationToken ct);

    /// <summary>
    /// Вставляє рядок, лише якщо рядків за <paramref name="where"/> менше ніж <paramref name="maxCount"/> і не порушено
    /// унікальність (F8-02). Перевірка й вставка атомарні щодо інших записів у таблицю.
    /// </summary>
    Task<DataInsertResult> InsertIfAsync(DataTableInfo table, JsonObject values, IReadOnlyList<DataFilter> where, int maxCount, CancellationToken ct);

    /// <summary>Атомарно додає <paramref name="by"/> до числової колонки (порожнє — 0); <c>null</c> — рядка немає.</summary>
    Task<DataRow?> IncrementAsync(DataTableInfo table, string key, string column, decimal by, CancellationToken ct);

    /// <summary>Кількість рядків за умовами.</summary>
    Task<int> CountRowsAsync(DataTableInfo table, IReadOnlyList<DataFilter> where, CancellationToken ct);
}

/// <summary>Помилка даних таблиці: невідома колонка, тип, ліміт, дубль ключа. Повідомлення — для людини.</summary>
public sealed class DataTableException : EngineException
{
    /// <summary>Створює помилку.</summary>
    /// <param name="code">Машинний код (<c>DATA_TABLE_*</c>).</param>
    /// <param name="message">Пояснення.</param>
    public DataTableException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Машинний код.</summary>
    public string Code { get; }
}
