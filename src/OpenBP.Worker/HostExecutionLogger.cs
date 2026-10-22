using Microsoft.Extensions.Logging;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using EngineLogLevel = OpenBP.Engine.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace OpenBP.Worker;

/// <summary>
/// <see cref="IExecutionLogger"/> воркера: повідомлення движка і вузлів іде в системний лог з id виконання і вузла,
/// уже очищене від секретів виконання (docs/architecture.md §8). Структуровані дані не пишуться — у них можуть бути items.
/// </summary>
/// <param name="logger">Системний лог.</param>
/// <param name="executionId">Виконання.</param>
/// <param name="scrubber">Scrubber секретів виконання.</param>
internal sealed partial class HostExecutionLogger(ILogger logger, Guid executionId, SecretScrubber scrubber) : IExecutionLogger
{
    /// <inheritdoc />
    public void Log(EngineLogLevel level, string? nodeId, string message, object? data = null)
    {
        var msLevel = level switch
        {
            EngineLogLevel.Debug => MsLogLevel.Debug,
            EngineLogLevel.Info => MsLogLevel.Information,
            EngineLogLevel.Warning => MsLogLevel.Warning,
            _ => MsLogLevel.Error,
        };
        if (logger.IsEnabled(msLevel))
        {
            Write(logger, msLevel, executionId, nodeId ?? "-", scrubber.Scrub(message));
        }
    }

    [LoggerMessage(Message = "Виконання {ExecutionId}, вузол {NodeId}: {Message}")]
    private static partial void Write(ILogger logger, MsLogLevel level, Guid executionId, string nodeId, string message);
}
