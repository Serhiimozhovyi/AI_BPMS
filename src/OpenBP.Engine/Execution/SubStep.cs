using System.Text.Json.Nodes;

namespace OpenBP.Engine.Execution;

/// <summary>
/// Підкрок усередині запуску вузла (F6-12): запит до моделі чи виклик інструмента агентом. Зберігається разом із кроком
/// у <c>execution_steps.sub_steps</c> (jsonb, docs/architecture.md §6) і проходить той самий scrubbing секретів.
/// </summary>
/// <param name="Kind">Вид: <see cref="SubStepKinds.Llm"/> або <see cref="SubStepKinds.Tool"/>.</param>
/// <param name="Name">Що саме: модель або «конектор / дія».</param>
/// <param name="StartedAt">Початок (UTC).</param>
/// <param name="FinishedAt">Завершення (UTC).</param>
/// <param name="Input">Вхід (аргументи інструмента); <c>null</c> — не зберігається.</param>
/// <param name="Output">Результат (відповідь моделі чи інструмента, скорочений).</param>
/// <param name="Error">Текст помилки; <c>null</c> — успіх.</param>
/// <param name="ItemIndex">Індекс вхідного item'а вузла.</param>
/// <param name="Iteration">Номер ітерації циклу агента, від 1.</param>
/// <param name="Usage">Токени запиту до моделі (<c>{ input, output }</c>), якщо провайдер їх повернув.</param>
public sealed record SubStep(
    string Kind,
    string Name,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    JsonNode? Input = null,
    JsonNode? Output = null,
    string? Error = null,
    int ItemIndex = 0,
    int Iteration = 1,
    JsonObject? Usage = null);

/// <summary>Види <see cref="SubStep"/>.</summary>
public static class SubStepKinds
{
    /// <summary>Запит до мовної моделі.</summary>
    public const string Llm = "llm";

    /// <summary>Виклик інструмента (tool конектора).</summary>
    public const string Tool = "tool";
}
