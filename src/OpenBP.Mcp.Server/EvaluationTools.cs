using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.Evaluations;

namespace OpenBP.Mcp.Server;

/// <summary>Результат <c>list_evaluations</c>.</summary>
/// <param name="Items">Оцінки в порядку створення.</param>
public sealed record EvaluationList(IReadOnlyList<EvaluationSummary> Items);

/// <summary>Tools оцінок якості вузлів ШІ (F6-17) поверх <see cref="IEvaluationService"/>; права — ті самі, що в REST.</summary>
[McpServerToolType]
public sealed class EvaluationTools
{
    /// <summary><c>list_evaluations</c>.</summary>
    /// <param name="evaluations">Сервіс оцінок.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="nodeId">Вузол.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "list_evaluations", Title = "List evaluations", ReadOnly = true, UseStructuredContent = true)]
    [Description("Lists evaluations (test case sets for an AI node) of a workflow with case count and the last run's score and workflow version.")]
    public static async Task<EvaluationList> ListEvaluations(
        IEvaluationService evaluations,
        [Description("Workflow id.")] Guid workflowId,
        [Description("Only evaluations of this node.")] string? nodeId = null,
        CancellationToken ct = default) =>
        new(await evaluations.ListAsync(workflowId, nodeId, ct).ConfigureAwait(false));

    /// <summary><c>create_evaluation</c>.</summary>
    /// <param name="evaluations">Сервіс оцінок.</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="nodeId">Вузол.</param>
    /// <param name="name">Назва.</param>
    /// <param name="cases">Кейси.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "create_evaluation", Title = "Create evaluation", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description(
        "Creates an evaluation for a node of the current workflow version (usually ai.llm or ai.agent): a set of cases " +
        "{ name?, input: [{\"json\": {...}}], expected?, check, config? }. Checks: exact (actual equals expected), " +
        "jsonPath (config.path like \"text\" or \"items[0].name\" equals expected), expression (config.expression returns true; " +
        "$json is the actual output, $node[\"expected\"].json the expected one; non-objects are under .value), " +
        "llm (a judge model: config.credentialId, config.model, config.criteria). The actual output is the json of the single item " +
        "on output 0, or an array of jsons for several items. Errors: EVALUATION_INVALID.")]
    public static Task<EvaluationDetail> CreateEvaluation(
        IEvaluationService evaluations,
        [Description("Workflow id.")] Guid workflowId,
        [Description("Id of the node to evaluate.")] string nodeId,
        [Description("Cases.")] EvaluationCaseInput[] cases,
        [Description("Evaluation name.")] string? name = null,
        CancellationToken ct = default) =>
        evaluations.CreateAsync(workflowId, new EvaluationCreate(nodeId, name, cases), ct);

    /// <summary><c>run_evaluation</c>.</summary>
    /// <param name="evaluations">Сервіс оцінок.</param>
    /// <param name="id">Оцінка.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "run_evaluation", Title = "Run evaluation", Destructive = false, Idempotent = false, UseStructuredContent = true)]
    [Description(
        "Runs every case of an evaluation on the current workflow version, one by one, the same way as test_node with items, " +
        "checks the results and stores the run. Returns passed/total, the version and per-case results (actual, reason, error, " +
        "durationMs, tokens, executionId). Compare with earlier runs via list_evaluations. Requires the editor role.")]
    public static Task<EvaluationRun> RunEvaluation(
        IEvaluationService evaluations,
        [Description("Evaluation id.")] Guid id,
        CancellationToken ct = default) =>
        evaluations.RunAsync(id, ct);
}
