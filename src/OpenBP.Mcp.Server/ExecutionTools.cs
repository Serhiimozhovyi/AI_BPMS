using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.Executions;
using OpenBP.Application.Paging;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Server;

/// <summary>Tools виконань (ADR-002) поверх <see cref="IExecutionService"/>; права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class ExecutionTools
{
    /// <summary>Скільки чекати фінального стану в <c>run_workflow</c> за замовчуванням.</summary>
    public const int DefaultWaitSec = 60;

    /// <summary>Найбільше дозволене очікування в <c>run_workflow</c>.</summary>
    public const int MaxWaitSec = 600;

    /// <summary><c>run_workflow</c>.</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="events">Потік подій виконання.</param>
    /// <param name="id">Сценарій.</param>
    /// <param name="startNodeId">Тригер.</param>
    /// <param name="startItems">Items тригера.</param>
    /// <param name="wait">Чекати фінального стану.</param>
    /// <param name="timeoutSec">Скільки чекати.</param>
    /// <param name="correlationKey">Ключ кореляції.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "run_workflow", Title = "Run workflow", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description(
        "Runs the current version of a workflow manually (nodes with pinData return their pinned items instead of executing). " +
        "With wait=true (default) waits up to timeoutSec (default 60, max 600) for a final state and returns the execution with all steps; " +
        "if the time runs out, returns the execution as it is then (state queued/running) — poll it with get_execution. " +
        "With wait=false returns immediately in state queued. Step data is already scrubbed of secrets. " +
        "correlationKey ties the execution to a deal or customer; with settings.correlation.skipIfActive a run while another one with the same key is active " +
        "is recorded as cancelled with the reason.")]
    public static async Task<ExecutionDetail> RunWorkflow(
        IExecutionService executions,
        IExecutionEventSource events,
        [Description("Workflow id.")] Guid id,
        [Description("Trigger node id to start from; omit to start from every node without incoming connections.")] string? startNodeId = null,
        [Description("Items the trigger fires with (e.g. a webhook body as [{\"json\": {...}}]); requires startNodeId.")] Item[]? startItems = null,
        [Description("Wait for the execution to finish (or to reach state waiting — a long core.wait).")] bool wait = true,
        [Description("How long to wait, in seconds (1–600).")] int timeoutSec = DefaultWaitSec,
        [Description("Correlation key (up to 200 characters), e.g. a deal id.")] string? correlationKey = null,
        CancellationToken ct = default)
    {
        var started = await executions.StartAsync(id, new StartExecutionRequest(startNodeId, startItems, CorrelationKey: correlationKey), ct).ConfigureAwait(false);
        if (wait)
        {
            await WaitForFinalStateAsync(events, started.Id, TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, MaxWaitSec)), ct).ConfigureAwait(false);
        }

        return await executions.GetAsync(started.Id, ct).ConfigureAwait(false);
    }

    /// <summary><c>get_execution</c>.</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="id">Виконання.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_execution", Title = "Get execution", ReadOnly = true, UseStructuredContent = true)]
    [Description("Returns an execution with its state, error and steps (input/output items per port of every executed node; same as the resource openbp://executions/{id}) " +
        "and `children`: sub-workflow executions it started via core.executeWorkflow (mode sub, parentNodeId).")]
    public static Task<ExecutionDetail> GetExecution(
        IExecutionService executions,
        [Description("Execution id.")] Guid id,
        CancellationToken ct = default) =>
        executions.GetAsync(id, ct);

    /// <summary><c>list_executions</c>.</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="status">Стан.</param>
    /// <param name="correlationKey">Ключ кореляції.</param>
    /// <param name="page">Сторінка.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_executions", Title = "List executions", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists executions without steps, most recently started first, 50 per page.")]
    public static Task<PageResult<ExecutionSummary>> ListExecutions(
        IExecutionService executions,
        [Description("Only executions of this workflow.")] Guid? workflowId = null,
        [Description("Only in this state: queued, running, waiting, cancelling, success, error, cancelled, timed_out.")] string? status = null,
        [Description("Page number, starting at 1.")] int page = 1,
        [Description("Only executions with this correlation key (exact match), e.g. all processes of one deal.")] string? correlationKey = null,
        CancellationToken ct = default) =>
        executions.ListAsync(
            new ExecutionFilter(workflowId, status is null ? null : ExecutionStates.FromDb(status), CorrelationKey: correlationKey),
            new PageRequest { Page = page },
            ct);

    /// <summary><c>cancel_execution</c>.</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="id">Виконання.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "cancel_execution", Title = "Cancel execution", Destructive = true, Idempotent = true, UseStructuredContent = true)]
    [Description(
        "Cancels an execution: queued or waiting becomes cancelled at once, running becomes cancelling and stops at the worker's next heartbeat. " +
        "Does nothing for a finished execution.")]
    public static Task<ExecutionSummary> CancelExecution(
        IExecutionService executions,
        [Description("Execution id.")] Guid id,
        CancellationToken ct = default) =>
        executions.CancelAsync(id, ct);

    /// <summary><c>resume_execution</c> (F6-08).</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="id">Виконання.</param>
    /// <param name="items">Нові items на вхід 0 вузла, що впав.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "resume_execution", Title = "Resume execution from failed node", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description(
        "Continues a failed (error or timed_out) execution from the node that failed: a new execution (mode resume) of the same " +
        "workflow version; nodes that already succeeded are not executed again, their outputs are reused. The failed node runs " +
        "with the same input, or with `items` as its input 0 (fix the data and resume). Returns the new execution, queued; " +
        "poll it with get_execution.")]
    public static Task<ExecutionSummary> ResumeExecution(
        IExecutionService executions,
        [Description("Id of the failed execution.")] Guid id,
        [Description("New input items for input 0 of the failed node, e.g. [{\"json\": {...}}]; omit to reuse the original input.")] Item[]? items = null,
        CancellationToken ct = default) =>
        executions.ResumeFromFailedAsync(id, items, ct);

    /// <summary>Чекає фінального стану чи очікування (<c>waiting</c>) за потоком подій; вихід за <paramref name="timeout"/> — не помилка.</summary>
    internal static async Task WaitForFinalStateAsync(IExecutionEventSource events, Guid executionId, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            await foreach (var e in events.SubscribeAsync(executionId, afterStepId: null, deadline.Token).ConfigureAwait(false))
            {
                if (e.Kind == ExecutionEventKind.Status && e.State is { } state && ExecutionStates.IsSettled(state))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Час очікування вийшов — віддаємо виконання як є.
        }
    }
}
