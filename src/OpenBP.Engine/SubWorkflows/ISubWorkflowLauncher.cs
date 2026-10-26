using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.SubWorkflows;

/// <summary>Запит на дочірнє виконання (<c>core.executeWorkflow</c>, F6-04).</summary>
/// <param name="WorkflowId">Дочірній сценарій.</param>
/// <param name="ParentExecutionId">Виконання, з якого викликано.</param>
/// <param name="ParentNodeId">Вузол <c>core.executeWorkflow</c>, що викликав.</param>
/// <param name="Items">Items на вихід <c>core.workflowTrigger</c> дочірнього сценарію.</param>
public sealed record SubWorkflowStart(Guid WorkflowId, Guid ParentExecutionId, string ParentNodeId, IReadOnlyList<Item> Items);

/// <summary>Створене дочірнє виконання.</summary>
/// <param name="ExecutionId">Дочірнє виконання.</param>
/// <param name="WorkflowName">Назва дочірнього сценарію — для повідомлень вузла.</param>
public sealed record SubWorkflowStarted(Guid ExecutionId, string WorkflowName);

/// <summary>Фінальний стан дочірнього виконання.</summary>
public enum SubWorkflowStatus
{
    /// <summary>Успіх.</summary>
    Success = 0,

    /// <summary>Помилка вузла чи воркера.</summary>
    Error = 1,

    /// <summary>Скасовано.</summary>
    Cancelled = 2,

    /// <summary>Перевищено таймаут сценарію.</summary>
    TimedOut = 3,
}

/// <summary>Результат завершеного дочірнього виконання.</summary>
/// <param name="Status">Фінальний стан.</param>
/// <param name="Items">Для <see cref="SubWorkflowStatus.Success"/> — вихід останнього успішного вузла; інакше порожньо.</param>
/// <param name="Error">Повідомлення помилки виконання (без секретів); <c>null</c> — немає.</param>
public sealed record SubWorkflowOutcome(SubWorkflowStatus Status, IReadOnlyList<Item> Items, string? Error);

/// <summary>
/// Запуск підсценаріїв для <c>core.executeWorkflow</c> (F6-04). Хост дає реалізацію: воркер — над Postgres (дочірнє
/// виконання <c>mode = sub</c> через <c>IExecutionLauncher</c>); CLI і тести — свою або жодної (тоді вузол дає помилку).
/// Помилки запуску (сценарію немає, немає <c>core.workflowTrigger</c>, items не за <c>inputSchema</c>, глибина) —
/// <see cref="SubWorkflowException"/>.
/// </summary>
public interface ISubWorkflowLauncher
{
    /// <summary>Найбільша глибина вкладення: виконання верхнього рівня — 0, його підсценарій — 1 і т. д.</summary>
    const int MaxDepth = 8;

    /// <summary>Створює дочірнє виконання поточної (опублікованої) версії сценарію.</summary>
    /// <param name="start">Що й звідки запускати.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="SubWorkflowException">Запуск неможливий.</exception>
    Task<SubWorkflowStarted> StartAsync(SubWorkflowStart start, CancellationToken ct);

    /// <summary>Чекає завершення дочірнього виконання не довше за <paramref name="timeout"/>.</summary>
    /// <param name="executionId">Дочірнє виконання.</param>
    /// <param name="timeout">Скільки чекати; <see cref="TimeSpan.Zero"/> — лише перевірити.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Результат або <c>null</c>, якщо виконання ще не завершилось.</returns>
    /// <exception cref="SubWorkflowException">Дочірнього виконання немає (видалено).</exception>
    Task<SubWorkflowOutcome?> WaitAsync(Guid executionId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Підсценарій неможливо запустити або його не знайдено; для движка — звичайна помилка вузла.</summary>
/// <param name="message">Пояснення українською за правилом «що сталося → що зробити».</param>
public sealed class SubWorkflowException(string message) : EngineException(message);
