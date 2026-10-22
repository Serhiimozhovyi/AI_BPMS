namespace OpenBP.Shared.Validation;

/// <summary>
/// Помилка валідації сценарію у формі <c>{ code, nodeId?, path?, message }</c>
/// (docs/workflow-format.md §«Валідація при збереженні»). ШІ-агент виправляє сценарій за <see cref="Code"/>.
/// </summary>
/// <param name="Code">Машинний код помилки, див. <see cref="WorkflowValidationCodes"/>.</param>
/// <param name="Message">Пояснення для людини.</param>
/// <param name="NodeId">Id вузла, до якого стосується помилка, якщо є.</param>
/// <param name="Path">JSON Pointer на місце в документі, напр. <c>/connections/2/to/node</c>.</param>
public sealed record WorkflowValidationError(string Code, string Message, string? NodeId = null, string? Path = null);

/// <summary>Коди помилок <see cref="WorkflowValidator"/> для правил 1–4.</summary>
public static class WorkflowValidationCodes
{
    /// <summary>Правило 1: документ не відповідає JSON Schema формату.</summary>
    public const string SchemaViolation = "SCHEMA_VIOLATION";

    /// <summary>Правило 2: два вузли з однаковим <c>id</c> — ребра неоднозначні.</summary>
    public const string NodeIdDuplicate = "NODE_ID_DUPLICATE";

    /// <summary>Правило 2: два вузли з однаковою <c>name</c> — вирази <c>$node["…"]</c> неоднозначні.</summary>
    public const string NodeNameDuplicate = "NODE_NAME_DUPLICATE";

    /// <summary>Правило 2: ребро посилається на вузол, якого немає.</summary>
    public const string ConnectionNodeUnknown = "CONNECTION_NODE_UNKNOWN";

    /// <summary>Правило 2: індекс виходу ребра більший за кількість виходів вузла.</summary>
    public const string ConnectionOutputUnknown = "CONNECTION_OUTPUT_UNKNOWN";

    /// <summary>Правило 2: індекс входу ребра більший за кількість входів вузла.</summary>
    public const string ConnectionInputUnknown = "CONNECTION_INPUT_UNKNOWN";

    /// <summary>Правило 3: граф містить цикл.</summary>
    public const string GraphCycle = "GRAPH_CYCLE";

    /// <summary>Правило 4: активний сценарій без жодного увімкненого тригера.</summary>
    public const string TriggerMissing = "TRIGGER_MISSING";
}
