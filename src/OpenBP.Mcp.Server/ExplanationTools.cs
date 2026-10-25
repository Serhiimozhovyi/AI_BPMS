using System.ComponentModel;
using ModelContextProtocol.Server;
using OpenBP.Application.Assistant;

namespace OpenBP.Mcp.Server;

/// <summary>
/// Tools пояснення сценарію простими словами (F3-73) поверх <see cref="IWorkflowExplanationService"/>: ШІ-агент
/// (Claude, ChatGPT), підключений через <c>/mcp</c>, пише пояснення сам, без моделі в «Налаштуваннях → Помічник».
/// </summary>
[McpServerToolType]
public sealed class ExplanationTools
{
    /// <summary>Як писати пояснення — однаково для агента і для моделі помічника (<see cref="WorkflowExplainer"/>).</summary>
    internal const string Guide =
        "Write it for a business user who does not read code, in the user's language (Ukrainian by default), as plain text " +
        "without Markdown symbols. Sections: «Коротко» — two or three sentences on what the process does and what starts it; " +
        "«Кроки» — a numbered list in the order the process runs (follow connections): for every enabled node its name in «», " +
        "what it does in business terms, which data it receives (from which step) and which data it passes on, each branch " +
        "of a decision with its condition, external systems by name; «Дані на вході і на виході» — what the process receives " +
        "and what it produces or sends; «Помилки й очікування» — waits, timeouts, error handling (omit if none). " +
        "Describe only what the definition shows.";

    /// <summary><c>get_workflow_explanation</c>.</summary>
    /// <param name="explanations">Сервіс пояснень.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "get_workflow_explanation", Title = "Get workflow explanation", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns the stored plain-language explanation of a workflow (what the whole process does, step by step, with data in and out) " +
        "— the same text the editor shows in «Пояснення процесу». `stale` = the text describes an older version; `status`: none, " +
        "pending, ready, failed, not_configured (no model in settings — write it yourself with save_workflow_explanation).")]
    public static Task<WorkflowExplanation> GetWorkflowExplanation(
        IWorkflowExplanationService explanations,
        [Description("Workflow id.")] Guid id,
        CancellationToken ct = default) =>
        explanations.GetAsync(id, ct);

    /// <summary><c>save_workflow_explanation</c>.</summary>
    /// <param name="explanations">Сервіс пояснень.</param>
    /// <param name="id">Id сценарію.</param>
    /// <param name="text">Пояснення.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "save_workflow_explanation", Title = "Save workflow explanation", Destructive = false, Idempotent = true, UseStructuredContent = true)]
    [Description(
        "Stores your plain-language explanation of the whole workflow for its current version; the editor shows it in «Пояснення процесу». " +
        "Call it after create_workflow or update_workflow (read the definition with get_workflow first) and whenever the user asks " +
        "to explain the process. " + Guide + " Up to 50000 characters. Moving nodes later keeps the explanation; any other change marks it stale.")]
    public static Task<WorkflowExplanation> SaveWorkflowExplanation(
        IWorkflowExplanationService explanations,
        [Description("Workflow id.")] Guid id,
        [Description("The explanation text.")] string text,
        CancellationToken ct = default) =>
        explanations.SaveAsync(id, text, ct);
}
