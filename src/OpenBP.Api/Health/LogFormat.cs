namespace OpenBP.Api.Health;

/// <summary>Формат журналу (F4-08): <c>OPENBP_LOG_FORMAT=json</c> — JSON-консоль зі scope; інакше — звичайна консоль.</summary>
public static class LogFormat
{
    /// <summary>Змінна формату журналу.</summary>
    public const string Key = "OPENBP_LOG_FORMAT";

    /// <summary>Налаштовує консольний журнал за <see cref="Key"/>; рівні — як завжди, <c>Logging__LogLevel__*</c>.</summary>
    public static ILoggingBuilder AddOpenBpConsole(this ILoggingBuilder logging, IConfiguration configuration)
    {
        if (string.Equals(configuration[Key], "json", StringComparison.OrdinalIgnoreCase))
        {
            logging.ClearProviders();
            logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        return logging;
    }
}
