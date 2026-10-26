using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Shared.Workflow;

/// <summary>
/// Вільна нотатка на канвасі (F3-62, docs/workflow-format.md §«Нотатки»): текст у Markdown, положення й розмір у
/// координатах канвасу, колір з Highlight-палітри AC-Kit. Не пов'язана з вузлами; валідатор перевіряє лише схему,
/// рушій нотатки ігнорує.
/// </summary>
public sealed class WorkflowNote
{
    /// <summary>Унікальний у межах сценарію; редактор генерує при створенні.</summary>
    public string Id { get; set; } = "";

    /// <summary>Текст нотатки в Markdown (порожній рядок дозволений — нотатку щойно додали).</summary>
    public string Text { get; set; } = "";

    /// <summary>Координати лівого верхнього кута на канвасі <c>[x, y]</c>, як <c>position</c> вузла.</summary>
    public double[] Position { get; set; } = [0, 0];

    /// <summary>Розмір <c>[ширина, висота]</c> у тих самих одиницях; <c>null</c> — розмір за замовчуванням редактора.</summary>
    public double[]? Size { get; set; }

    /// <summary>Одна з <see cref="NoteColors"/>; <c>null</c> — <see cref="NoteColors.Yellow"/>.</summary>
    public string? Color { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Кольори нотаток — назви Highlight-кольорів AC-Kit; відтінок (15 %) підбирає редактор.</summary>
public static class NoteColors
{
    public const string Yellow = "yellow";

    public const string Green = "green";

    public const string Blue = "blue";

    public const string Teal = "teal";

    public const string Orange = "orange";

    public const string Red = "red";

    public const string Violet = "violet";

    public static IReadOnlyList<string> All { get; } = [Yellow, Green, Blue, Teal, Orange, Red, Violet];
}
