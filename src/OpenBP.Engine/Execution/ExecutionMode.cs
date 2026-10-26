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

    /// <summary>Запуск сценарію помилок (<c>core.errorTrigger</c>) після помилки чи таймауту іншого сценарію (F4-06).</summary>
    Error = 3,

    /// <summary>
    /// Продовження помилкового виконання з вузла, що впав (F6-08): кроки вихідного виконання до цього вузла
    /// скопійовано в нове виконання, хост відновлює його через <c>ResumeAsync</c>.
    /// </summary>
    Resume = 4,

    /// <summary>
    /// Підсценарій (F6-04): дочірнє виконання, створене вузлом <c>core.executeWorkflow</c> іншого виконання; стартує з
    /// <c>core.workflowTrigger</c> з items батьківського вузла.
    /// </summary>
    Sub = 5,
}
