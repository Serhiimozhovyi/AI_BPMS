namespace OpenBP.Expressions;

/// <summary>
/// Ліміти пісочниці Jint (docs/architecture.md §5). Значення за замовчуванням — для виразів <c>{{ }}</c>;
/// Code-вузол використовує ті самі обмеження з власним таймаутом.
/// </summary>
public sealed record JintExpressionOptions
{
    /// <summary>Ліміти для виразів <c>{{ }}</c>: 50 мс, 16 МБ, рекурсія 64.</summary>
    public static JintExpressionOptions Default { get; } = new();

    /// <summary>Максимальний час обчислення одного виразу.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Максимум пам'яті, яку може виділити один вираз, у байтах.</summary>
    public long MemoryLimitBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Максимальна глибина рекурсії JS-функцій.</summary>
    public int RecursionLimit { get; init; } = 64;
}
