using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Projects;

/// <summary>Стани <see cref="PageActionResult"/>.</summary>
public static class PageActionStates
{
    /// <summary>Сценарій завершився успішно.</summary>
    public const string Success = "success";

    /// <summary>Сценарій завершився, але відмовив за правилом (<c>ok: false</c> у результаті), напр. «слот зайнятий».</summary>
    public const string Rejected = "rejected";

    /// <summary>Сценарій упав, скасований чи перевищив таймаут.</summary>
    public const string Error = "error";

    /// <summary>Сценарій ще виконується після очікування; результат з'явиться у виконаннях.</summary>
    public const string Running = "running";
}

/// <summary>
/// Результат дії сторінки чи форми, що запускала сценарій (F8-05).
/// </summary>
/// <param name="State"><see cref="PageActionStates"/>.</param>
/// <param name="Message">Повідомлення для людини: <c>message</c> з результату сценарію або текст помилки.</param>
/// <param name="Field">Для відмови форми: колонка, до якої належить повідомлення (<c>field</c> результату).</param>
/// <param name="Data">Перший елемент результату сценарію (вихід останнього успішного кроку); <c>null</c> — порожньо.</param>
/// <param name="ExecutionId">Виконання, за яким можна подивитися кроки.</param>
public sealed record PageActionResult(string State, string? Message, string? Field, JsonObject? Data, Guid? ExecutionId)
{
    /// <summary>Дія виконана без відмови.</summary>
    public bool Succeeded => State == PageActionStates.Success;
}

/// <summary>
/// Запуск сценарію проєкту з дії сторінки чи форми (F8-05, <c>docs/projects.md</c> §3.1): сценарій береться з цього
/// проєкту, стартує з <c>core.workflowTrigger</c> (або <c>core.manualTrigger</c>) з одним item <c>{ row, user }</c> чи
/// <c>{ values, user }</c>, а результатом є вихід його останнього успішного кроку. Ролей не перевіряє — це робить
/// <see cref="IProjectPageService"/>.
/// </summary>
public interface IPageWorkflowRunner
{
    /// <summary>Запускає сценарій і чекає результат до <see cref="PageWorkflowRunner.WaitSeconds"/> секунд.</summary>
    /// <param name="projectId">Проєкт сторінки: сценарій має бути в ньому.</param>
    /// <param name="workflow">Id або назва сценарію.</param>
    /// <param name="input">Вміст item: <c>row</c>/<c>values</c> і <c>user</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnprocessableException"><c>PAGE_WORKFLOW_UNKNOWN</c> — сценарію немає в проєкті; <c>PAGE_WORKFLOW_NO_TRIGGER</c> — немає тригера виклику.</exception>
    Task<PageActionResult> RunAsync(Guid projectId, string workflow, JsonObject input, CancellationToken ct);
}

/// <summary>Реалізація <see cref="IPageWorkflowRunner"/> поверх <see cref="IExecutionLauncher"/> і таблиць виконань.</summary>
public sealed class PageWorkflowRunner(OpenBpDbContext db, IExecutionLauncher launcher) : IPageWorkflowRunner
{
    /// <summary>Скільки секунд сторінка чекає результат сценарію.</summary>
    public const int WaitSeconds = 30;

    /// <summary>Сценарію немає в проєкті сторінки.</summary>
    public const string WorkflowUnknownCode = ProjectPageService.WorkflowUnknownCode;

    /// <summary>У сценарії немає вузла для виклику ззовні.</summary>
    public const string NoTriggerCode = "PAGE_WORKFLOW_NO_TRIGGER";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(150);

    private static readonly string[] TriggerTypes = ["core.workflowTrigger", "core.manualTrigger"];

    /// <inheritdoc />
    public async Task<PageActionResult> RunAsync(Guid projectId, string workflow, JsonObject input, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
        ArgumentNullException.ThrowIfNull(input);
        var target = await ResolveAsync(projectId, workflow, ct);
        var versionId = target.PublishedVersionId ?? target.CurrentVersionId
            ?? throw new UnprocessableException(NoTriggerCode, $"Сценарій «{target.Name}» без версії; збережіть і опублікуйте його.");
        var definition = WorkflowJson.Deserialize(await db.WorkflowVersions.AsNoTracking()
            .Where(v => v.Id == versionId).Select(v => v.Definition).SingleAsync(ct));
        var trigger = TriggerTypes
            .Select(type => definition.Nodes.FirstOrDefault(n => n.Type == type && n.Disabled != true))
            .FirstOrDefault(n => n is not null)
            ?? throw new UnprocessableException(
                NoTriggerCode, $"У сценарії «{target.Name}» немає увімкненого вузла «Виклик з іншого сценарію» (core.workflowTrigger). Додайте його й опублікуйте сценарій.");

        var started = await launcher.LaunchAsync(
            new ExecutionLaunch(target.Id, ExecutionMode.Trigger, trigger.Id, [new Item { Json = (JsonObject)input.DeepClone() }], VersionId: versionId)
            {
                AllowInactive = true,
                KeepSuccessfulData = true,
            },
            ct);
        return await WaitAsync(started.Id, ct);
    }

    private async Task<WorkflowRef> ResolveAsync(Guid projectId, string reference, CancellationToken ct)
    {
        var id = Guid.TryParse(reference, out var parsed) ? parsed : (Guid?)null;
        var lower = reference.Trim().ToLowerInvariant();
        return await db.Workflows.AsNoTracking()
                   .Where(w => w.GroupId == projectId && ((id != null && w.Id == id) || w.Name.ToLower() == lower))
                   .OrderBy(w => w.Name)
                   .Select(w => new WorkflowRef(w.Id, w.Name, w.PublishedVersionId, w.CurrentVersionId))
                   .FirstOrDefaultAsync(ct)
               ?? throw new UnprocessableException(WorkflowUnknownCode, $"Сценарію «{reference}» у проєкті немає.");
    }

    private async Task<PageActionResult> WaitAsync(Guid executionId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(WaitSeconds);
        while (true)
        {
            var execution = await db.Executions.AsNoTracking().Where(e => e.Id == executionId)
                .Select(e => new { e.Status, e.Error }).SingleOrDefaultAsync(ct);
            if (execution is null)
            {
                return new PageActionResult(PageActionStates.Error, "Виконання сценарію зникло; повторіть дію.", null, null, executionId);
            }

            switch (execution.Status)
            {
                case ExecutionStatuses.Success:
                    return await SuccessAsync(executionId, ct);
                case ExecutionStatuses.Error:
                case ExecutionStatuses.TimedOut:
                case ExecutionStatuses.Cancelled:
                    return new PageActionResult(PageActionStates.Error, execution.Error ?? "Сценарій не виконався.", null, null, executionId);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new PageActionResult(PageActionStates.Running, "Сценарій ще виконується; результат буде у виконаннях проєкту.", null, null, executionId);
            }

            await Task.Delay(Poll, ct);
        }
    }

    /// <summary>Вихід останнього успішного кроку: порт 0, а якщо він порожній — перший непорожній (як у підсценаріїв).</summary>
    private async Task<PageActionResult> SuccessAsync(Guid executionId, CancellationToken ct)
    {
        var output = await db.ExecutionSteps.AsNoTracking()
            .Where(s => s.ExecutionId == executionId && s.Status == StepStatuses.ToDb(StepStatus.Success))
            .OrderByDescending(s => s.Id)
            .Select(s => s.Output)
            .FirstOrDefaultAsync(ct);
        var ports = output is null ? null : JsonSerializer.Deserialize<Item[][]>(output, WorkflowJson.Options);
        var data = ports?.FirstOrDefault(p => p.Length > 0)?[0].Json;
        var message = data?["message"] is JsonValue m && m.TryGetValue<string>(out var text) ? text : null;
        var field = data?["field"] is JsonValue f && f.TryGetValue<string>(out var column) ? column : null;
        var rejected = data?["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var accepted) && !accepted;
        return new PageActionResult(rejected ? PageActionStates.Rejected : PageActionStates.Success, message, field, data, executionId);
    }

    private sealed record WorkflowRef(Guid Id, string Name, Guid? PublishedVersionId, Guid? CurrentVersionId);
}
