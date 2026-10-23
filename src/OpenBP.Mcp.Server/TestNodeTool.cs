using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Server;

/// <summary>Результат <c>test_node</c>.</summary>
/// <param name="ExecutionId">Виконання, в якому вузол виконувався (його можна відкрити <c>get_execution</c>).</param>
/// <param name="Status">Статус кроку вузла.</param>
/// <param name="Input">Items на входах вузла, по масиву на порт.</param>
/// <param name="Output">Items на виходах вузла, по масиву на порт.</param>
public sealed record TestNodeResult(Guid ExecutionId, StepStatus Status, Item[][] Input, Item[][] Output);

/// <summary>Вузол упав або не виконався в <c>test_node</c> — результат з <c>isError</c> (код <see cref="NodeFailedCode"/>).</summary>
public sealed class TestNodeFailedException(string message) : OpenBpException(NodeFailedCode, 422, message)
{
    /// <summary>Код помилки.</summary>
    public const string NodeFailedCode = "NODE_FAILED";
}

/// <summary><c>test_node</c> (ADR-002) — обгортка над <see cref="IExecutionService.StartNodeAsync"/>, тим самим механізмом, що «Виконати вузол» в UI.</summary>
[McpServerToolType]
public sealed class TestNodeTool
{
    /// <summary><c>test_node</c>.</summary>
    /// <param name="executions">Сервіс виконань.</param>
    /// <param name="events">Потік подій виконання.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="nodeId">Вузол.</param>
    /// <param name="items">Items на вході 0.</param>
    /// <param name="timeoutSec">Скільки чекати.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "test_node", Title = "Test node", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description(
        "Executes one node of the current version of a workflow and returns its input and output items per port. " +
        "With `items`, they are the node's input 0 and no other node runs. Without `items`, the node gets the same input as " +
        "in the last manual run (predecessors that succeeded then are not executed again; the others return their pinData or run). " +
        "The node itself always executes, even if it has pinData. A failing node is a tool error with code NODE_FAILED and its error message.")]
    public static async Task<TestNodeResult> TestNode(
        IExecutionService executions,
        IExecutionEventSource events,
        [Description("Workflow id.")] Guid workflowId,
        [Description("Id of the node to execute.")] string nodeId,
        [Description("Input items for input 0, e.g. [{\"json\": {...}}]; omit to reuse the last manual run.")] Item[]? items = null,
        [Description("How long to wait, in seconds (1–600).")] int timeoutSec = ExecutionTools.DefaultWaitSec,
        CancellationToken ct = default)
    {
        var started = await executions.StartNodeAsync(workflowId, nodeId, items, ct).ConfigureAwait(false);
        await ExecutionTools.WaitForFinalStateAsync(events, started.Id, TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 1, ExecutionTools.MaxWaitSec)), ct).ConfigureAwait(false);
        var detail = await executions.GetAsync(started.Id, ct).ConfigureAwait(false);

        var step = detail.Steps.LastOrDefault(s => s.NodeId == nodeId);
        if (step is null)
        {
            throw new TestNodeFailedException(
                $"Вузол «{nodeId}» не виконався (виконання {started.Id}: {ExecutionStates.ToDb(detail.Summary.State)}" +
                (detail.Summary.Error is { } error ? $", {error}" : "") + ").");
        }

        if (step.Status == StepStatus.Error)
        {
            throw new TestNodeFailedException(step.Error?.Message ?? $"Вузол «{nodeId}» завершився помилкою.");
        }

        return new TestNodeResult(started.Id, step.Status, step.Input, step.Output);
    }
}
