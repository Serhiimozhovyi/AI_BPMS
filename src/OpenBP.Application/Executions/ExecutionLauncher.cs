using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Executions;

/// <summary>
/// Реалізація <see cref="IExecutionLauncher"/> (docs/architecture.md §10.4): рядок <c>executions</c> у стані
/// <see cref="ExecutionState.Queued"/> і job <see cref="JobKinds.Execution"/> додаються одним <c>SaveChanges</c> —
/// однією транзакцією, тож воркер не побачить job без виконання і навпаки. Ролей не перевіряє.
/// </summary>
public sealed class ExecutionLauncher(OpenBpDbContext db, TimeProvider time) : IExecutionLauncher
{
    /// <summary>Код конфлікту: тригер спрацював для неактивного сценарію.</summary>
    public const string WorkflowInactiveCode = "WORKFLOW_INACTIVE";

    /// <summary>Код помилки валідації: <c>startNodeId</c> не знайдено в поточній версії сценарію.</summary>
    public const string StartNodeUnknownCode = "START_NODE_UNKNOWN";

    /// <summary>Код помилки валідації: <c>destinationNodeId</c> не знайдено в поточній версії сценарію.</summary>
    public const string DestinationNodeUnknownCode = "DESTINATION_NODE_UNKNOWN";

    /// <inheritdoc />
    public async Task<ExecutionSummary> LaunchAsync(ExecutionLaunch launch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.StartItems is not null && launch.StartNodeId is null)
        {
            throw new ArgumentException("startItems задаються лише разом із startNodeId.", nameof(launch));
        }

        if (launch.Mode != ExecutionMode.Manual && (launch.DestinationNodeId is not null || launch.RunData is { Count: > 0 }))
        {
            throw new ArgumentException("destinationNodeId і runData — лише для ручного запуску.", nameof(launch));
        }

        var workflow = await db.Workflows.AsNoTracking()
            .Where(w => w.Id == launch.WorkflowId)
            .Select(w => new { w.Active, w.CurrentVersionId })
            .SingleOrDefaultAsync(ct)
            ?? throw new NotFoundException("workflow", launch.WorkflowId);
        if (launch.Mode == ExecutionMode.Trigger && !workflow.Active)
        {
            throw new ConflictException(WorkflowInactiveCode, $"Сценарій «{launch.WorkflowId}» неактивний; тригери його не запускають.");
        }

        var versionId = workflow.CurrentVersionId
                        ?? throw new InvalidOperationException($"Сценарій «{launch.WorkflowId}» без поточної версії.");
        if (launch.StartNodeId is not null || launch.DestinationNodeId is not null)
        {
            var definition = await db.WorkflowVersions.AsNoTracking()
                .Where(v => v.Id == versionId)
                .Select(v => v.Definition)
                .SingleAsync(ct);
            var nodeIds = WorkflowJson.Deserialize(definition).Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            List<WorkflowValidationError> errors = [];
            if (launch.StartNodeId is { } startNodeId && !nodeIds.Contains(startNodeId))
            {
                errors.Add(new WorkflowValidationError(
                    StartNodeUnknownCode, $"Вузла «{startNodeId}» немає в поточній версії сценарію.", startNodeId, "startNodeId"));
            }

            if (launch.DestinationNodeId is { } destinationNodeId && !nodeIds.Contains(destinationNodeId))
            {
                errors.Add(new WorkflowValidationError(
                    DestinationNodeUnknownCode, $"Вузла «{destinationNodeId}» немає в поточній версії сценарію.", destinationNodeId, "destinationNodeId"));
            }

            if (errors.Count > 0)
            {
                throw new WorkflowValidationException(errors);
            }
        }

        var now = time.GetUtcNow();
        var execution = new ExecutionEntity
        {
            Id = Guid.CreateVersion7(now),
            WorkflowId = launch.WorkflowId,
            VersionId = versionId,
            Mode = ExecutionModes.ToDb(launch.Mode),
            Status = ExecutionStatuses.Queued,
        };
        var payload = new ExecutionJobPayload
        {
            ExecutionId = execution.Id,
            WorkflowId = launch.WorkflowId,
            VersionId = versionId,
            Mode = launch.Mode,
            StartNodeId = launch.StartNodeId,
            StartItems = launch.StartItems,
            DestinationNodeId = launch.DestinationNodeId,
            RunData = launch.RunData is { Count: > 0 } runData ? runData : null,
        };
        db.Executions.Add(execution);
        db.Jobs.Add(new JobEntity
        {
            Id = Guid.CreateVersion7(now),
            Kind = JobKinds.Execution,
            Payload = JobJson.Serialize(payload),
            RunAt = now,
        });
        await db.SaveChangesAsync(ct);
        return ExecutionMapping.ToSummary(execution);
    }
}
