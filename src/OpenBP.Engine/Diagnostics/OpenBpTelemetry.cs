using System.Diagnostics;

namespace OpenBP.Engine.Diagnostics;

/// <summary>
/// Трейси OpenTelemetry (F6-18, docs/admin/README.md §10): один <see cref="ActivitySource"/> на весь AI_BPMS.
/// Span-и: виконання (<see cref="ExecutionSpan"/>, воркер), вузол (<see cref="NodeSpan"/>, движок), виклик MCP tool
/// (<see cref="McpToolSpan"/>) і HTTP до моделі (<see cref="LlmSpan"/>). Атрибути — лише ідентифікатори й назви:
/// параметри вузлів, items, аргументи tool'ів, промпти й credentials у span-и не потрапляють, а помилка дає статус
/// <see cref="ActivityStatusCode.Error"/> з типом винятку без його тексту (у тексті може бути відповідь сервісу).
/// Без підписника (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c> не задано) <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// повертає <c>null</c> і нічого не коштує.
/// </summary>
public static class OpenBpTelemetry
{
    /// <summary>Назва джерела для <c>AddSource</c>.</summary>
    public const string SourceName = "OpenBP";

    /// <summary>Span виконання сценарію.</summary>
    public const string ExecutionSpan = "openbp.execution";

    /// <summary>Span виконання вузла.</summary>
    public const string NodeSpan = "openbp.node";

    /// <summary>Span виклику MCP tool.</summary>
    public const string McpToolSpan = "openbp.mcp.tool";

    /// <summary>Span запиту до моделі.</summary>
    public const string LlmSpan = "openbp.llm";

    /// <summary>Атрибут: <c>executions.id</c>.</summary>
    public const string ExecutionId = "openbp.execution.id";

    /// <summary>Атрибут: <c>workflows.id</c>.</summary>
    public const string WorkflowId = "openbp.workflow.id";

    /// <summary>Атрибут: режим виконання (<c>manual</c>, <c>trigger</c>, …).</summary>
    public const string ExecutionMode = "openbp.execution.mode";

    /// <summary>Атрибут: підсумковий статус виконання чи кроку.</summary>
    public const string Status = "openbp.status";

    /// <summary>Атрибут: id вузла.</summary>
    public const string NodeId = "openbp.node.id";

    /// <summary>Атрибут: назва вузла.</summary>
    public const string NodeName = "openbp.node.name";

    /// <summary>Атрибут: тип вузла (<c>core.set</c>, …).</summary>
    public const string NodeType = "openbp.node.type";

    /// <summary>Атрибут: номер спроби вузла (з 1).</summary>
    public const string NodeAttempt = "openbp.node.attempt";

    /// <summary>Атрибут: id MCP-сервера.</summary>
    public const string McpServerId = "openbp.mcp.server.id";

    /// <summary>Атрибут: назва MCP tool.</summary>
    public const string McpTool = "openbp.mcp.tool";

    /// <summary>Атрибут (семантичні конвенції GenAI): провайдер моделі.</summary>
    public const string GenAiSystem = "gen_ai.system";

    /// <summary>Атрибут (семантичні конвенції GenAI): запитана модель.</summary>
    public const string GenAiModel = "gen_ai.request.model";

    /// <summary>Джерело span-ів AI_BPMS.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Починає дочірній span і копіює в нього <see cref="ExecutionId"/>/<see cref="WorkflowId"/> з найближчого
    /// предка, щоб кожен span виконання мав їх в атрибутах.
    /// </summary>
    /// <param name="name">Назва span-а.</param>
    /// <param name="kind">Тип span-а.</param>
    public static Activity? StartChild(string name, ActivityKind kind = ActivityKind.Internal)
    {
        var parent = Activity.Current;
        var activity = Source.StartActivity(name, kind);
        if (activity is not null && parent is not null)
        {
            foreach (var key in (ReadOnlySpan<string>)[ExecutionId, WorkflowId])
            {
                if (activity.GetTagItem(key) is null && Inherited(parent, key) is { } value)
                {
                    activity.SetTag(key, value);
                }
            }
        }

        return activity;
    }

    /// <summary>
    /// Позначає span помилковим: лише тип винятку, без тексту. Повертає <c>true</c>, тож годиться для фільтра
    /// <c>catch … when (OpenBpTelemetry.Fail(span, ex) &amp;&amp; …)</c>, який не перехоплює виняток сам.
    /// </summary>
    /// <param name="activity">Span або <c>null</c>.</param>
    /// <param name="error">Виняток.</param>
    public static bool Fail(Activity? activity, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
        return true;
    }

    private static object? Inherited(Activity? activity, string key)
    {
        for (; activity is not null; activity = activity.Parent)
        {
            if (activity.GetTagItem(key) is { } value)
            {
                return value;
            }
        }

        return null;
    }
}
