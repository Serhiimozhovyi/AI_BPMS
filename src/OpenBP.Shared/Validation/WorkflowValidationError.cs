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

/// <summary>Коди помилок <see cref="WorkflowValidator"/> (правила 1–4) і провайдерів <see cref="INodeSchemaProvider"/> (правила 5–6).</summary>
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

    /// <summary>Правило 5: тип або версія вузла невідомі — параметри перевірити неможливо.</summary>
    public const string NodeTypeUnknown = "NODE_TYPE_UNKNOWN";

    /// <summary>Правило 5: параметри вузла не відповідають його схемі.</summary>
    public const string ParameterInvalid = "PARAMETER_INVALID";

    /// <summary>Правила 5–6: <c>serverId</c> не зареєстровано або tool <c>toolName</c> не знайдено в кеші сервера.</summary>
    public const string McpToolUnknown = "MCP_TOOL_UNKNOWN";

    /// <summary>Активація: шлях <c>core.webhook</c> уже зайнятий іншим активним сценарієм або іншим вузлом цього.</summary>
    public const string WebhookPathConflict = "WEBHOOK_PATH_CONFLICT";

    /// <summary><c>core.webhook</c> з <c>respond: "fromNode"</c>, після якого немає увімкненого <c>core.respondToWebhook</c> (F6-05).</summary>
    public const string WebhookRespondNodeMissing = "WEBHOOK_RESPOND_NODE_MISSING";

    /// <summary><c>settings.executionTimeoutSec</c> більший за серверну межу <c>OPENBP_MAX_EXECUTION_TIMEOUT_SEC</c>.</summary>
    public const string ExecutionTimeoutTooLarge = "EXECUTION_TIMEOUT_TOO_LARGE";

    /// <summary><c>settings.errorWorkflowId</c>: не id, сам сценарій, сценарію немає або в ньому немає <c>core.errorTrigger</c>.</summary>
    public const string ErrorWorkflowInvalid = "ERROR_WORKFLOW_INVALID";

    /// <summary>Бізнес-розмітка: дві доріжки <c>business.lanes</c> з однаковим <c>id</c>.</summary>
    public const string BusinessLaneIdDuplicate = "BUSINESS_LANE_ID_DUPLICATE";

    /// <summary>Бізнес-розмітка: два зовнішні кроки <c>business.steps</c> з однаковим <c>id</c>.</summary>
    public const string BusinessStepIdDuplicate = "BUSINESS_STEP_ID_DUPLICATE";

    /// <summary>Бізнес-розмітка: <c>lane</c> вузла або кроку посилається на доріжку, якої немає в <c>business.lanes</c>.</summary>
    public const string BusinessLaneUnknown = "BUSINESS_LANE_UNKNOWN";

    /// <summary>Бізнес-розмітка: зв'язок зовнішнього кроку <c>links[].node</c> посилається на вузол, якого немає.</summary>
    public const string BusinessLinkNodeUnknown = "BUSINESS_LINK_NODE_UNKNOWN";

    /// <summary>Бізнес-розмітка: зовнішній крок без жодного зв'язку з вузлом — на схемі він нічого не означає.</summary>
    public const string BusinessStepLinksMissing = "BUSINESS_STEP_LINKS_MISSING";

    /// <summary>Етапи (ADR-004): два етапи <c>stages</c> з однаковим <c>id</c>.</summary>
    public const string StageIdDuplicate = "STAGE_ID_DUPLICATE";

    /// <summary>Етапи: <c>stages[].nodes[]</c> посилається на вузол, якого немає.</summary>
    public const string StageNodeUnknown = "STAGE_NODE_UNKNOWN";

    /// <summary>Етапи: вузол належить двом етапам — стан етапу був би неоднозначним.</summary>
    public const string StageNodeInTwoStages = "STAGE_NODE_IN_TWO_STAGES";

    /// <summary>
    /// Етапи: вузли етапу не зв'язані між собою в графі (шлях може йти лише через технічні вузли без етапу) —
    /// етап мав би кілька незалежних частин, і його стан був би неоднозначним.
    /// </summary>
    public const string StageNotConnected = "STAGE_NOT_CONNECTED";

    /// <summary>Етапи: <c>transitions[].to</c> посилається на етап, якого немає.</summary>
    public const string StageTransitionUnknown = "STAGE_TRANSITION_UNKNOWN";

    /// <summary>Етапи: два правила одного етапу з однаковим <c>id</c> — порушення й відхилення неоднозначні.</summary>
    public const string StageRuleIdDuplicate = "STAGE_RULE_ID_DUPLICATE";

    /// <summary>
    /// <c>serverId</c>/<c>credentialId</c> посилається на конектор чи credential іншого проєкту (F6-14): сценарій може
    /// використовувати лише спільні ресурси і ресурси свого проєкту.
    /// </summary>
    public const string ProjectReferenceForbidden = "PROJECT_REFERENCE_FORBIDDEN";

    /// <summary>Попередження (не помилка): <c>$vars.key</c> посилається на змінну інстансу, якої немає (F6-09).</summary>
    public const string VariableUnknown = "VARIABLE_UNKNOWN";
}
