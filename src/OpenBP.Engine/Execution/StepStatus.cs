namespace OpenBP.Engine.Execution;

/// <summary>Результат одного запуску вузла (поле <c>execution_steps.status</c>).</summary>
public enum StepStatus
{
    /// <summary>Вузол виконано, вихід збережено.</summary>
    Success = 0,

    /// <summary>Вузол завершився помилкою після всіх повторів; <see cref="StepRecord.Error"/> заповнено.</summary>
    Error = 1,

    /// <summary>Вузол не виконувався: на входах не було items і <c>alwaysRun</c> не задано (гілка згасла).</summary>
    Skipped = 2,

    /// <summary>Виконання скасовано (<see cref="CancellationToken"/> або таймаут) під час роботи вузла.</summary>
    Cancelled = 3,

    /// <summary>
    /// Вузол призупинив виконання до <see cref="StepRecord.ResumeAt"/> (довге очікування, F5-02); на пробудженні він
    /// виконується знову з <see cref="Nodes.INodeContext.Resumption"/>.
    /// </summary>
    Waiting = 4,
}
