using System.Text.Json;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>Рядки <c>executions</c>/<c>execution_steps</c> → DTO application-шару.</summary>
internal static class ExecutionMapping
{
    public static ExecutionSummary ToSummary(ExecutionEntity e) => new(
        e.Id,
        e.WorkflowId,
        e.VersionId,
        ExecutionModes.FromDb(e.Mode),
        ExecutionStates.FromDb(e.Status),
        e.StartedAt,
        e.FinishedAt,
        e.Error);

    /// <summary>Крок як збережений: items і помилка вже очищені від секретів (F2-04).</summary>
    public static ExecutionStepDto ToStep(ExecutionStepEntity e) => new(
        e.Id,
        e.NodeId,
        e.RunIndex,
        StepStatuses.FromDb(e.Status),
        ReadItems(e.Input),
        ReadItems(e.Output),
        e.Error is null ? null : JsonSerializer.Deserialize<StepError>(e.Error, WorkflowJson.Options),
        e.StartedAt ?? DateTimeOffset.MinValue,
        e.FinishedAt ?? DateTimeOffset.MinValue);

    private static Item[][] ReadItems(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Item[][]>(json, WorkflowJson.Options) ?? [];
}
