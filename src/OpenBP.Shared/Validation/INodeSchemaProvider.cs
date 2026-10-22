using OpenBP.Shared.Workflow;

namespace OpenBP.Shared.Validation;

/// <summary>
/// Джерело знань про типи вузлів для <see cref="WorkflowValidator"/>: кількість портів (правило 2)
/// і перевірка параметрів та посилань (правила 5–6 з docs/workflow-format.md §«Валідація»).
/// Реалізації — у F1-08 (<c>OpenBP.Mcp</c>, схеми tools з кешу) і для <c>core.*</c>-вузлів з їхніх Descriptor.
/// </summary>
public interface INodeSchemaProvider
{
    /// <summary>
    /// Кількість входів і виходів типу вузла за <c>Descriptor.Inputs/Outputs</c>,
    /// без додаткового виходу <c>error</c> (його валідатор додає сам для <c>continueOnFail: true</c>).
    /// <c>null</c> — тип невідомий провайдеру; тоді межі портів не перевіряються (лише індекси ≥ 0 за схемою).
    /// </summary>
    NodePorts? GetPorts(string type, int typeVersion);

    /// <summary>
    /// Правила 5–6 для одного вузла: параметри відповідають схемі вузла (рядок <c>{{ }}</c> допустимий
    /// замість будь-якого типу; для <c>mcp.tool</c> — <c>inputSchema</c> tool'а, інакше <c>MCP_TOOL_UNKNOWN</c>),
    /// а <c>serverId</c>/<c>credentialId</c> існують і доступні користувачу.
    /// Порожній список — порушень немає.
    /// </summary>
    Task<IReadOnlyList<WorkflowValidationError>> ValidateNodeAsync(NodeDefinition node, CancellationToken ct);
}

/// <summary>Кількість вхідних і вихідних портів типу вузла.</summary>
/// <param name="Inputs">Кількість входів (0 для тригерів).</param>
/// <param name="Outputs">Кількість виходів без <c>error</c>.</param>
public sealed record NodePorts(int Inputs, int Outputs);

/// <summary>
/// Заглушка до появи реальних провайдерів (F1-08): нічого не знає про типи вузлів,
/// тому правила 5–6 і межі портів пропускаються.
/// </summary>
public sealed class NullNodeSchemaProvider : INodeSchemaProvider
{
    public static NullNodeSchemaProvider Instance { get; } = new();

    private NullNodeSchemaProvider()
    {
    }

    public NodePorts? GetPorts(string type, int typeVersion) => null;

    public Task<IReadOnlyList<WorkflowValidationError>> ValidateNodeAsync(NodeDefinition node, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WorkflowValidationError>>([]);
}
