using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Engine.Diagnostics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OpenBP.Worker;

/// <summary>
/// Експорт трейсів OpenTelemetry з воркера (F6-18, docs/admin/README.md §10): вмикається змінною
/// <see cref="EndpointKey"/>, решту стандартних <c>OTEL_EXPORTER_OTLP_*</c> (протокол, заголовки) читає сам експортер.
/// Експортуються лише span-и <see cref="OpenBpTelemetry.SourceName"/>.
/// </summary>
public static class WorkerTelemetry
{
    /// <summary>Адреса OTLP-колектора (<c>http://otel-collector:4317</c>); не задано — трейси вимкнено.</summary>
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Назва сервісу в трейсах; типово <see cref="DefaultServiceName"/>.</summary>
    public const string ServiceNameKey = "OTEL_SERVICE_NAME";

    /// <summary>Назва сервісу за замовчуванням.</summary>
    public const string DefaultServiceName = "openbp-worker";

    /// <summary>Додає OpenTelemetry з OTLP-експортером, якщо задано <see cref="EndpointKey"/>.</summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configuration">Конфігурація (змінні середовища).</param>
    public static IServiceCollection AddOpenBpTracing(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration[EndpointKey]))
        {
            return services;
        }

        var serviceName = configuration[ServiceNameKey] is { Length: > 0 } name ? name : DefaultServiceName;
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(OpenBpTelemetry.SourceName)
                .AddOtlpExporter());
        return services;
    }
}
