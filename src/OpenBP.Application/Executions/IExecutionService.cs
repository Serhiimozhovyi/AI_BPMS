using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Paging;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Виконання для користувача (docs/architecture.md §10.1, §10.4): REST <c>/executions</c>, UI, MCP-tools
/// <c>run_workflow</c>/<c>get_execution</c>/<c>list_executions</c>. Реалізація — F2-09, повтор — F3-08.
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

    /// <summary>
    /// «Виконати вузол» (F2-21; MCP <c>test_node</c>): ручний запуск лише вузла <paramref name="nodeId"/> і його попередників
    /// через <see cref="StartAsync"/> з <see cref="StartExecutionRequest.DestinationNodeId"/> і
    /// <see cref="StartExecutionRequest.RunData"/>. Звідки вхід вузла:
    /// <list type="bullet">
    /// <item><paramref name="items"/> задано — це items на вході 0: вони стають виходом попередника першого ребра на вхід 0,
    /// решта попередників не виконується (порожній вихід);</item>
    /// <item>інакше — як у UI: попередники, що завершились успішно (або згаслою гілкою) в останньому ручному запуску
    /// сценарію, не виконуються і віддають свій тодішній вихід; решта — з <c>pinData</c> або виконуються.</item>
    /// </list>
    /// Сам вузол виконується завжди, навіть якщо для нього є <c>pinData</c>.
    /// </summary>
    /// <param name="workflowId">Сценарій; виконується поточна версія.</param>
    /// <param name="nodeId">Вузол.</param>
    /// <param name="items">Items на вході 0; <c>null</c> — дані останнього ручного запуску.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Виконання в стані <see cref="ExecutionState.Queued"/>.</returns>
    /// <exception cref="NotFoundException">Сценарію немає.</exception>
    /// <exception cref="WorkflowValidationException">Вузла немає в поточній версії (<c>DESTINATION_NODE_UNKNOWN</c>).</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> задано для вузла без вхідних ребер.</exception>
    Task<ExecutionSummary> StartNodeAsync(Guid workflowId, string nodeId, IReadOnlyList<Item>? items, CancellationToken ct);

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

    /// <summary>Повторний запуск тієї самої версії сценарію з тими самими стартовими даними (<see cref="Engine.Execution.ExecutionMode.Retry"/>, docs/architecture.md §10.4).</summary>
    /// <param name="id">Виконання, яке повторити; має бути у фінальному стані.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Нове виконання в стані <see cref="ExecutionState.Queued"/>.</returns>
    /// <exception cref="NotFoundException">Виконання немає.</exception>
    /// <exception cref="ConflictException">Виконання ще не завершене (код <c>EXECUTION_NOT_FINISHED</c>).</exception>
    Task<ExecutionSummary> RetryAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Продовжити помилкове (<c>error</c>/<c>timed_out</c>) виконання з вузла, що впав (<see cref="Engine.Execution.ExecutionMode.Resume"/>,
    /// F6-08): нове виконання тієї самої версії, успішні вузли не виконуються знову. Роль editor.
    /// </summary>
    /// <param name="id">Вихідне виконання.</param>
    /// <param name="items">Нові items на вхід 0 вузла, що впав; <c>null</c> — ті самі, що були.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Виконання немає.</exception>
    /// <exception cref="ConflictException"><c>EXECUTION_NOT_FAILED</c> — стан не <c>error</c>/<c>timed_out</c>.</exception>
    /// <exception cref="WorkflowValidationException"><c>RESUME_INPUT_NOT_APPLICABLE</c> — <paramref name="items"/> нікуди подати.</exception>
    Task<ExecutionSummary> ResumeFromFailedAsync(Guid id, IReadOnlyList<Item>? items, CancellationToken ct);
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
