namespace OpenBP.Engine;

/// <summary>
/// Журнал виконання (docs/architecture.md §2): повідомлення від движка і вузлів, прив'язані до виконання і вузла.
/// Хост вирішує, куди їх писати (SSE, консоль CLI, системний лог). Секрети у повідомлення не передаються —
/// реалізація додатково застосовує scrubbing (docs/architecture.md §8).
/// Реалізації: у воркері/CLI і <c>InMemoryExecutionLogger</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface IExecutionLogger
{
    /// <summary>Записує повідомлення.</summary>
    /// <param name="level">Рівень.</param>
    /// <param name="nodeId">Id вузла, до якого стосується повідомлення; <c>null</c> — повідомлення на рівні виконання.</param>
    /// <param name="message">Текст для людини.</param>
    /// <param name="data">Довільні структуровані дані (серіалізуються реалізацією); без секретів.</param>
    void Log(LogLevel level, string? nodeId, string message, object? data = null);
}

/// <summary>Рівень повідомлення <see cref="IExecutionLogger"/>. Свій enum, щоб движок не залежав від Microsoft.Extensions.Logging.</summary>
public enum LogLevel
{
    /// <summary>Діагностика; показується лише в режимі налагодження.</summary>
    Debug = 0,

    /// <summary>Звичайний перебіг: вузол стартував, завершився тощо.</summary>
    Info = 1,

    /// <summary>Щось пішло не так, але виконання триває (повтор, <c>continueOnFail</c>).</summary>
    Warning = 2,

    /// <summary>Помилка, через яку вузол або виконання завершилось невдало.</summary>
    Error = 3,
}
