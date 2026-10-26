namespace OpenBP.Engine.Execution;

/// <summary>Стан етапу бізнес-процесу у виконанні (ADR-004 §2).</summary>
public enum StageStatus
{
    /// <summary>Жоден вузол етапу ще не стартував.</summary>
    Pending = 0,

    /// <summary>Вузол етапу стартував (працює, чекає або завершився), а перехід з етапу ще не пройдено.</summary>
    Active = 1,

    /// <summary>Пройдено перехід з етапу або останній вузол етапу завершився без вихідних ребер.</summary>
    Done = 2,

    /// <summary>Виконання завершилось, а етап так і не став активним (гілка не дійшла).</summary>
    Skipped = 3,

    /// <summary>Виконання скасовано, перевищило таймаут чи завершилось помилкою, поки етап був активним.</summary>
    Cancelled = 4,
}

/// <summary>
/// Стан одного етапу виконання — рядок <c>execution_stages</c>. Підстан «прострочено» не зберігається, а
/// обчислюється на момент читання: <see cref="IsOverdue"/>.
/// </summary>
/// <param name="StageId">Id етапу (<c>stages[].id</c>).</param>
/// <param name="Name">Назва етапу на момент виконання.</param>
/// <param name="Position">Порядковий номер етапу в <c>stages[]</c>, з 0 — «Етап N» у журналі.</param>
/// <param name="Status">Стан етапу.</param>
/// <param name="EnteredAt">Коли стартував перший вузол етапу; <c>null</c> — етап не починався.</param>
/// <param name="DueAt">Строк етапу за <c>due</c>; <c>null</c> — строку немає або етап не починався.</param>
/// <param name="ExitedAt">Коли етап завершився (<c>done</c>/<c>cancelled</c>); інакше <c>null</c>.</param>
public sealed record StageState(
    string StageId,
    string Name,
    int Position,
    StageStatus Status,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? DueAt,
    DateTimeOffset? ExitedAt)
{
    /// <summary>Етап активний, а строк <see cref="DueAt"/> уже минув на момент <paramref name="now"/>.</summary>
    /// <param name="now">Поточний час.</param>
    public bool IsOverdue(DateTimeOffset now) => Status == StageStatus.Active && DueAt is { } due && now > due;
}
