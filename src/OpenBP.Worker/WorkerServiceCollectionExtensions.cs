using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Ai;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Nodes.Core;

namespace OpenBP.Worker;

/// <summary>Реєстрація залежностей воркера.</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>Секція конфігурації з <see cref="JobQueueOptions"/> (<c>LockTimeout</c>, <c>MaxAttempts</c>, …).</summary>
    public const string QueueSection = WorkerOptions.Section + ":Queue";

    /// <summary>
    /// Додає воркер виконань: БД (рядок підключення <c>Default</c>), <see cref="JobQueue"/>, credentials (ключ з
    /// <c>OPENBP_ENCRYPTION_KEY</c> — без нього старт неможливий), реєстри MCP-серверів і вузлів, Jint і
    /// <see cref="ExecutionWorker"/> з налаштуваннями секції <see cref="WorkerOptions.Section"/>, а також
    /// <see cref="ExecutionRecoveryService"/> (відновлення після падіння воркера) з налаштуваннями черги з підсекції
    /// <see cref="QueueSection"/>.
    /// </summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configuration">Конфігурація.</param>
    public static IServiceCollection AddOpenBpWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new WorkerOptions();
        configuration.GetSection(WorkerOptions.Section).Bind(options);
        options.Validate();
        services.AddSingleton(options);

        var queueOptions = new JobQueueOptions();
        configuration.GetSection(QueueSection).Bind(queueOptions);
        queueOptions.Validate();
        if (options.HeartbeatInterval >= queueOptions.LockTimeout)
        {
            throw new ArgumentException(
                $"{WorkerOptions.Section}:{nameof(WorkerOptions.HeartbeatInterval)} має бути меншим за {QueueSection}:{nameof(JobQueueOptions.LockTimeout)}, інакше lock протухатиме під час виконання.");
        }

        services.AddSingleton(queueOptions);

        services.AddDbContextFactory<OpenBpDbContext>(o => o.UseNpgsql(configuration.GetConnectionString("Default")));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new JobQueue(sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<JobQueueOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => CredentialEncryptionKey.FromEnvironment());
        services.AddSingleton<CredentialStore>();

        services.AddSingleton(sp => new McpServerRegistry(loggerFactory: sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<IMcpServerRegistry>(sp => sp.GetRequiredService<McpServerRegistry>());
        services.AddSingleton<McpServerSync>();

        // Таймаутом запиту до моделі керує сам вузол ai.llm (timeoutSec), тож HttpClient без власного таймауту.
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        services.AddSingleton<INodeRegistry>(sp => new WorkerNodeRegistry(
            CoreNodes.All()
                .Concat(McpNodes.All(sp.GetRequiredService<IMcpServerRegistry>()))
                .Concat(AiNodes.All(sp.GetRequiredService<HttpClient>()))));
        services.AddSingleton<IExpressionEvaluator>(_ => new JintExpressionEvaluator());

        services.AddSingleton(sp => new ExecutionJobProcessor(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<McpServerSync>(),
            sp.GetRequiredService<IExpressionEvaluator>(),
            sp.GetService<IBinaryStore>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExecutionJobProcessor>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new ExecutionWorker(
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<ExecutionJobProcessor>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExecutionWorker>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new ExecutionRecoveryService(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<JobQueueOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExecutionRecoveryService>>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
