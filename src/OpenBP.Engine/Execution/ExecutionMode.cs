using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Execution;

/// <summary>Режим запуску виконання (поле <c>executions.mode</c>, docs/architecture.md §6).</summary>
public enum ExecutionMode
{
    /// <summary>Ручний тестовий запуск з канвасу або CLI; дозволено використовувати <see cref="WorkflowDefinition.PinData"/>.</summary>
    Manual = 0,

    /// <summary>Запуск тригером (webhook, розклад, <c>mcp.poll</c>) у продуктивному режимі.</summary>
    Trigger = 1,

    /// <summary>Повторний запуск із журналу виконань.</summary>
    Retry = 2,
}
