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
        e.Error,
        e.ResumedFromExecutionId,
        e.CorrelationKey,
        e.Status == ExecutionStatuses.Waiting ? e.ResumeAt : null,
        e.Status == ExecutionStatuses.Waiting ? e.WaitingNodeId : null,
        e.ParentExecutionId,
        e.ParentNodeId);

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
        e.FinishedAt ?? DateTimeOffset.MinValue,
        e.SubSteps is null ? null : JsonSerializer.Deserialize<SubStep[]>(e.SubSteps, WorkflowJson.Options),
        e.ResumeAt);

    /// <summary>
    /// Етап як збережений, узгоджений зі станом виконання: виконання, що завершилось без воркера (скасування з черги чи
    /// очікування), лишає етапи <c>pending</c>/<c>active</c> — їх віддаємо як <c>skipped</c>/<c>cancelled</c>.
    /// </summary>
    public static ExecutionStageDto ToStage(ExecutionStageEntity e, ExecutionEntity execution, DateTimeOffset now)
    {
        var status = StageStatuses.FromDb(e.Status);
        var exitedAt = e.ExitedAt;
        var state = ExecutionStates.FromDb(execution.Status);
        if (ExecutionStates.IsFinal(state))
        {
            if (status == StageStatus.Pending)
            {
                status = StageStatus.Skipped;
            }
            else if (status == StageStatus.Active)
            {
                status = state == ExecutionState.Success ? StageStatus.Done : StageStatus.Cancelled;
                exitedAt ??= execution.FinishedAt;
            }
        }

        var overdue = status == StageStatus.Active && e.DueAt is { } due && now > due;
        return new(e.StageId, e.Name, e.Position, status, overdue, e.EnteredAt, e.DueAt, exitedAt);
    }

    public static ViolationDto ToViolation(ViolationEntity v) =>
        new(v.Id, v.StageId, v.RuleId, v.Mode, v.Status, v.Message, v.Reason, v.ActorUserId, v.CreatedAt, v.ResolvedAt);

    private static Item[][] ReadItems(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<Item[][]>(json, WorkflowJson.Options) ?? [];
}
