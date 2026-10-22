using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;

namespace OpenBP.Application.Executions;

/// <summary>
/// Виконання для користувача (docs/architecture.md §10.1, §10.4): REST <c>/executions</c>, UI, MCP-tools
/// <c>run_workflow</c>/<c>get_execution</c>/<c>list_executions</c>. Реалізація — F2-09 (повтор — F3-G).
/// Ролі: читання — <see cref="UserRole.Viewer"/>; запуск, скасування, повтор — <see cref="UserRole.Editor"/>.
/// Кроки віддаються такими, як збережені, — уже очищені від секретів (F2-04).
/// </summary>
public interface IExecutionService
{
    /// <summary>Ручний запуск поточної версії сценарію (<see cref="Engine.Execution.ExecutionMode.Manual"/>, з <c>pinData</c>) через <see cref="IExecutionLauncher"/>.</summary>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="request">Стартовий вузол і items; <c>null</c> — усі тригери без вхідних ребер.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Виконання в стані <see cref="ExecutionState.Queued"/>; за його id одразу можна відкрити SSE.</returns>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="WorkflowValidationException">Поточна версія некоректна (напр. невідомий <c>startNodeId</c>).</exception>
    Task<ExecutionSummary> StartAsync(Guid workflowId, StartExecutionRequest? request, CancellationToken ct);

    /// <summary>Сторінка списку, сортування за <c>started_at desc</c> (для <see cref="ExecutionState.Queued"/> — за <c>id</c>).</summary>
    /// <param name="filter">Фільтр.</param>
    /// <param name="page">Пагінація.</param>
    /// <param name="ct">Скасування.</param>
    Task<PageResult<ExecutionSummary>> ListAsync(ExecutionFilter filter, PageRequest page, CancellationToken ct);

    /// <summary>Виконання з кроками.</summary>
    /// <param name="id"><c>executions.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Виконання немає.</exception>
    Task<ExecutionDetail> GetAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Скасування: <see cref="ExecutionState.Queued"/> → одразу <see cref="ExecutionState.Cancelled"/>;
    /// <see cref="ExecutionState.Running"/> → <see cref="ExecutionState.Cancelling"/>, воркер зупинить виконання на
    /// найближчому heartbeat. Для фінального стану — нічого не змінює (ідемпотентно).
    /// </summary>
    /// <param name="id"><c>executions.id</c>.</param>
    /// <param name="ct">Скасування запиту.</param>
    /// <returns>Виконання після запиту.</returns>
    /// <exception cref="NotFoundException">Виконання немає.</exception>
    Task<ExecutionSummary> CancelAsync(Guid id, CancellationToken ct);

    /// <summary>Повторний запуск тієї самої версії сценарію з тими самими стартовими даними (<see cref="Engine.Execution.ExecutionMode.Retry"/>). F3-G.</summary>
    /// <param name="id">Виконання, яке повторити; має бути у фінальному стані.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Нове виконання в стані <see cref="ExecutionState.Queued"/>.</returns>
    /// <exception cref="NotFoundException">Виконання немає.</exception>
    /// <exception cref="ConflictException">Виконання ще не завершене (код <c>EXECUTION_NOT_FINISHED</c>).</exception>
    Task<ExecutionSummary> RetryAsync(Guid id, CancellationToken ct);
}

/// <summary>
/// Внутрішній вхід для запуску (docs/architecture.md §10.4): створює рядок <c>executions</c> зі станом
/// <see cref="ExecutionState.Queued"/> і job <see cref="Jobs.JobKinds.Execution"/> з
/// <see cref="Jobs.ExecutionJobPayload"/> в одній транзакції. Ролей не перевіряє — викликачі: webhook (F2-10),
/// Quartz (F2-11), <c>mcp.poll</c> (F2-12), <see cref="IExecutionService"/>. Реалізація — F2-09.
/// </summary>
public interface IExecutionLauncher
{
    /// <summary>Ставить виконання поточної версії сценарію в чергу.</summary>
    /// <param name="launch">Що запустити.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Виконання в стані <see cref="ExecutionState.Queued"/>.</returns>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="ConflictException">Сценарій неактивний для <see cref="Engine.Execution.ExecutionMode.Trigger"/> (код <c>WORKFLOW_INACTIVE</c>).</exception>
    Task<ExecutionSummary> LaunchAsync(ExecutionLaunch launch, CancellationToken ct);
}

/// <summary>
/// Потік подій виконання для SSE і MCP-ресурсу <c>openbp://executions/{id}</c> (docs/architecture.md §10.5):
/// опитування <c>execution_steps</c> курсором за <c>id</c> і рядка <c>executions</c> кожні 500 мс. Реалізація — F2-09.
/// </summary>
public interface IExecutionEventSource
{
    /// <summary>
    /// Події після курсора: спершу всі вже збережені кроки з <c>id &gt; afterStepId</c>, далі нові в міру появи
    /// і зміни стану; фінальний стан — остання подія, після неї потік завершується. Для вже завершеного виконання
    /// потік віддає кроки після курсора, стан і завершується одразу.
    /// </summary>
    /// <param name="executionId"><c>executions.id</c>.</param>
    /// <param name="afterStepId">Курсор (<c>Last-Event-ID</c>); <c>null</c> — з початку.</param>
    /// <param name="ct">Скасування (клієнт закрив з'єднання).</param>
    /// <exception cref="NotFoundException">Виконання немає.</exception>
    IAsyncEnumerable<ExecutionEvent> SubscribeAsync(Guid executionId, Guid? afterStepId, CancellationToken ct);
}
