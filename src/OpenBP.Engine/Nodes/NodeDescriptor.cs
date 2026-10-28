using System.Text.Json.Nodes;

namespace OpenBP.Engine.Nodes;

/// <summary>
/// Опис типу вузла для реєстру, валідатора і палітри канвасу (docs/connectors.md §1).
/// Незмінний; один екземпляр на тип+версію.
/// </summary>
/// <param name="Type">Тип вузла <c>&lt;пакет&gt;.&lt;вузол&gt;</c>, напр. <c>core.if</c>.</param>
/// <param name="Version">Версія типу (≥ 1); збільшується при несумісній зміні параметрів, стара версія лишається.</param>
/// <param name="DisplayName">Назва для палітри, українською.</param>
/// <param name="Group">Група в палітрі.</param>
/// <param name="Inputs">Назви вхідних портів у порядку індексів; порожній список — тригер.</param>
/// <param name="Outputs">Назви вихідних портів у порядку індексів (без <c>error</c> — його додає движок для <c>continueOnFail</c>).</param>
/// <param name="ParametersSchema">JSON Schema параметрів вузла; за нею будується форма і валідуються параметри (правило 5).</param>
/// <param name="Credentials">Credentials, які вузол запитує через <see cref="INodeContext.GetCredentialAsync"/>.</param>
/// <param name="Description">Опис для палітри і документації; необов'язковий.</param>
/// <param name="PlanKind">
/// Роль вузла в плані процесу для стислого каталогу (<c>trigger|action|condition|approval|wait|transform|notify</c>);
/// <c>null</c> — визначається за типом. У REST/MCP-описі не віддається.
/// </param>
public sealed record NodeDescriptor(
    string Type,
    int Version,
    string DisplayName,
    NodeGroup Group,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    JsonObject ParametersSchema,
    IReadOnlyList<CredentialRequirement>? Credentials = null,
    string? Description = null,
    [property: System.Text.Json.Serialization.JsonIgnore] string? PlanKind = null)
{
    /// <summary>Вузол без входів — тригер (правило 4 валідації: активний сценарій має ≥ 1 тригер).</summary>
    public bool IsTrigger => Inputs.Count == 0;
}

/// <summary>Група вузла в палітрі канвасу.</summary>
public enum NodeGroup
{
    /// <summary>Тригери: <c>core.manualTrigger</c>, <c>core.webhook</c>, <c>core.schedule</c>, <c>mcp.poll</c>.</summary>
    Trigger = 0,

    /// <summary>Логіка: <c>core.if</c>, <c>core.switch</c>, <c>core.merge</c>, <c>core.loop</c>, <c>core.wait</c>.</summary>
    Logic = 1,

    /// <summary>Перетворення даних: <c>core.set</c>, <c>core.code</c>.</summary>
    Transform = 2,

    /// <summary>Інтеграції через MCP: <c>mcp.tool</c>.</summary>
    Integration = 3,

    /// <summary>Мовні моделі: <c>ai.llm</c>.</summary>
    Ai = 4,
}

/// <summary>
/// Credential, потрібний вузлу. Id credential'а береться з параметра вузла <see cref="Parameter"/>
/// (у форматі workflow секретів немає — лише <c>credentialId</c>).
/// </summary>
/// <param name="Name">Назва вимоги, за якою вузол викликає <see cref="INodeContext.GetCredentialAsync"/>.</param>
/// <param name="Type">Тип credential'а (<c>credentials.type</c>), напр. <c>anthropic</c>, <c>basic</c>; UI фільтрує список за ним.</param>
/// <param name="Parameter">Назва параметра вузла, що містить id credential'а.</param>
/// <param name="Required">Чи обов'язковий credential для роботи вузла.</param>
public sealed record CredentialRequirement(
    string Name,
    string Type,
    string Parameter = "credentialId",
    bool Required = true);
