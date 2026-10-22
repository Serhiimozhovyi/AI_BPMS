using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>
/// Збережений результат одного запуску вузла (рядок таблиці <c>execution_steps</c>, docs/architecture.md §6).
/// Items на входах і виходах — за портами, в порядку <c>Descriptor.Inputs</c> / <c>Descriptor.Outputs</c>.
/// </summary>
public sealed record StepRecord
{
    /// <summary>Ідентифікатор виконання.</summary>
    public required Guid ExecutionId { get; init; }

    /// <summary>Стабільний id вузла з формату workflow (<see cref="NodeDefinition.Id"/>).</summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Порядковий номер запуску вузла в межах виконання, від 0. Більше 0 — лише для вузлів, які
    /// виконуються повторно (наприклад, тіло <c>core.loop</c>).
    /// </summary>
    public int RunIndex { get; init; }

    /// <summary>Результат запуску.</summary>
    public required StepStatus Status { get; init; }

    /// <summary>Items на кожному вхідному порту (порожній масив для тригерів).</summary>
    public Item[][] Input { get; init; } = [];

    /// <summary>
    /// Items на кожному вихідному порту. Для <see cref="StepStatus.Error"/> з <c>continueOnFail: true</c>
    /// останній порт — <c>error</c> з одним item, що описує помилку.
    /// </summary>
    public Item[][] Output { get; init; } = [];

    /// <summary>Помилка вузла; заповнюється для <see cref="StepStatus.Error"/>, інакше <c>null</c>.</summary>
    public StepError? Error { get; init; }

    /// <summary>Початок запуску вузла (UTC).</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Завершення запуску вузла (UTC).</summary>
    public required DateTimeOffset FinishedAt { get; init; }
}

/// <summary>Помилка вузла у вигляді, придатному для журналу й виходу <c>error</c>. Секретів не містить.</summary>
/// <param name="Message">Повідомлення для людини.</param>
/// <param name="Type">Тип помилки: назва винятку або код (<c>MCP_TOOL_UNKNOWN</c>, <c>TIMEOUT</c> тощо).</param>
/// <param name="Attempts">Скільки спроб зроблено з урахуванням повторів.</param>
public sealed record StepError(string Message, string? Type = null, int Attempts = 1);
