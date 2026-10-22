namespace OpenBP.Application.Paging;

/// <summary>Offset-пагінація списків (docs/architecture.md §10.3): <c>?page=1&amp;pageSize=50</c>.</summary>
public sealed record PageRequest
{
    /// <summary>Розмір сторінки за замовчуванням.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Найбільший дозволений розмір сторінки; більше значення обрізається до нього.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Номер сторінки, з 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Кількість елементів на сторінці.</summary>
    public int PageSize { get; init; } = DefaultPageSize;

    /// <summary>Той самий запит з номером і розміром сторінки в допустимих межах.</summary>
    public PageRequest Normalized() => new()
    {
        Page = Math.Max(1, Page),
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize),
    };

    /// <summary>Скільки елементів пропустити (для нормалізованого запиту).</summary>
    public int Skip => (Math.Max(1, Page) - 1) * Math.Clamp(PageSize, 1, MaxPageSize);
}

/// <summary>Сторінка списку: <c>{ items, page, pageSize, total }</c>.</summary>
/// <typeparam name="T">Тип елемента.</typeparam>
/// <param name="Items">Елементи сторінки.</param>
/// <param name="Page">Номер сторінки, з 1.</param>
/// <param name="PageSize">Розмір сторінки.</param>
/// <param name="Total">Загальна кількість елементів за фільтром.</param>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
