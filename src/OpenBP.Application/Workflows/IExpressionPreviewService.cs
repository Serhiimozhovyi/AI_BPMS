using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Попередній перегляд виразу <c>{{ }}</c> у редакторі (F3-56, docs/architecture.md §5): обчислює рядок параметра тим самим
/// обчислювачем і з тими самими лімітами, що й виконання, але без <c>$env</c> і credentials.
/// </summary>
public interface IExpressionPreviewService
{
    /// <summary>Обчислює вираз для вузла <paramref name="workflowId"/>/<see cref="EvaluateExpressionRequest.NodeId"/>.</summary>
    /// <param name="workflowId">Сценарій; його поточна версія дає часовий пояс і <c>pinData</c>.</param>
    /// <param name="request">Вираз і, за бажання, вхідний item та виходи вузлів з редактора.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Значення виразу або текст помилки (помилка виразу — не виняток).</returns>
    /// <exception cref="Errors.NotFoundException">Сценарію немає.</exception>
    Task<ExpressionPreviewResult> EvaluateAsync(Guid workflowId, EvaluateExpressionRequest request, CancellationToken ct);
}

/// <summary>Запит попереднього перегляду виразу.</summary>
/// <param name="Expression">Рядок параметра, можливо з кількома <c>{{ }}</c>.</param>
/// <param name="NodeId">Вузол, у параметрі якого вираз; може бути ще не збереженим.</param>
/// <param name="Item">Вхідний item (<c>$json</c>); без нього — перший item <c>pinData</c> попереднього вузла або <c>{}</c>.</param>
/// <param name="Nodes">Виходи вузлів за назвою (<c>$node["…"]</c>); без них — <c>pinData</c> збереженої версії.</param>
public sealed record EvaluateExpressionRequest(
    string Expression,
    string? NodeId = null,
    Item? Item = null,
    Dictionary<string, List<Item>>? Nodes = null);

/// <summary>Результат: <see cref="Value"/> або <see cref="Error"/>.</summary>
/// <param name="Value">Значення виразу зі збереженням типу; <c>null</c> — вираз дав <c>null</c> або помилку.</param>
/// <param name="Error">Повідомлення про синтаксичну помилку, помилку виконання чи ліміт; <c>null</c> — успіх.</param>
public sealed record ExpressionPreviewResult(JsonNode? Value, string? Error);
