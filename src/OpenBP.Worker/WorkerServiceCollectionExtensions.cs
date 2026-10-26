using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Ai;
using OpenBP.Application.Assistant;
using OpenBP.Application.DataTables;
using OpenBP.Application.Executions;
using OpenBP.Application.Webhooks;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Data;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Nodes.Core;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker;

/// <summary>Реєстрація залежностей воркера.</summary>
public static class WorkerServiceCollectionExtensions
{
    /// <summary>Секція конфігурації з <see cref="JobQueueOptions"/> (<c>LockTimeout</c>, <c>MaxAttempts</c>, …).</summary>
    public const string QueueSection = WorkerOptions.Section + ":Queue";

    /// <summary>
    /// Додає воркер виконань: БД (рядок підключення <c>Default</c>), <see cref="JobQueue"/>, credentials (ключ з
    /// <c>OPENBP_ENCRYPTION_KEY</c> — без нього старт неможливий), реєстри MCP-серверів і вузлів, Jint і
    /// <see cref="ExecutionWorker"/> (виконання, опитування <c>mcp.poll</c> — <see cref="PollJobProcessor"/>, пояснення сценаріїв —
    /// <see cref="ExplainJobProcessor"/>) з налаштуваннями секції <see cref="WorkerOptions.Section"/>, а також
    /// <see cref="ExecutionRecoveryService"/> (відновлення після падіння воркера) з налаштуваннями черги з підсекції
    /// <see cref="QueueSection"/>; файли — у <see cref="S3BinaryStore"/> (<c>Storage:S3:Bucket</c>) або <see cref="LocalBinaryStore"/>
    /// (<c>Storage:LocalPath</c>); трейси — <see cref="WorkerTelemetry.AddOpenBpTracing"/>.
    /// </summary>
    /// <param name="services">Колекція сервісів.</param>
    /// <param name="configuration">Конфігурація.</param>
    /// <summary>Змінна з публічною адресою OpenBP (спільна з Api, F6-01).</summary>
    public const string PublicUrlKey = "OPENBP_PUBLIC_URL";

    public static IServiceCollection AddOpenBpWorker(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new WorkerOptions();
        configuration.GetSection(WorkerOptions.Section).Bind(options);
        // Публічна адреса — та сама змінна, що й в Api (docs/admin/env.md), секція Worker може її перекрити.
        options.PublicUrl ??= configuration[PublicUrlKey];
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
        services.AddSingleton(WorkflowLimits.Parse(configuration[WorkflowLimits.MaxExecutionTimeoutKey]));

        var storage = new BinaryStorageOptions();
        configuration.GetSection(BinaryStorageOptions.Section).Bind(storage);
        if (!string.IsNullOrWhiteSpace(storage.S3.Bucket))
        {
            services.AddSingleton(sp => S3BinaryStore.Create(storage.S3, sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IBinaryStore>(sp =>
            {
                var s3 = sp.GetRequiredService<S3BinaryStore>();
                var readers = new Dictionary<string, IBinaryStore>(StringComparer.Ordinal) { [S3BinaryStore.StorageName] = s3 };
                if (!string.IsNullOrWhiteSpace(storage.LocalPath))
                {
                    readers[LocalBinaryStore.StorageName] = new LocalBinaryStore(storage.LocalPath, sp.GetRequiredService<TimeProvider>());
                }

                return new RoutingBinaryStore(s3, readers);
            });
        }
        else if (!string.IsNullOrWhiteSpace(storage.LocalPath))
        {
            services.AddSingleton<IBinaryStore>(sp => new LocalBinaryStore(storage.LocalPath, sp.GetRequiredService<TimeProvider>()));
        }

        services.AddOpenBpTracing(configuration);

        services.AddDbContextFactory<OpenBpDbContext>(o => o.UseNpgsql(configuration.GetConnectionString("Default")));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new WorkerLiveness(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<WorkerMetrics>();
        services.AddSingleton(sp => new JobQueue(sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(), sp.GetRequiredService<JobQueueOptions>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(_ => CredentialEncryptionKey.FromEnvironment());
        // Оновлення oauth2-токенів при читанні credential'а (F4-04): власний HttpClient з таймаутом.
        services.AddSingleton(_ => new OAuth2Client(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));
        services.AddSingleton<CredentialStore>();

        services.AddSingleton(sp => new McpServerRegistry(loggerFactory: sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddSingleton<IMcpServerRegistry>(sp => sp.GetRequiredService<McpServerRegistry>());
        services.AddSingleton<McpServerSync>();

        // Таймаутом запиту до моделі керує сам вузол ai.llm (timeoutSec), тож HttpClient без власного таймауту.
        services.AddSingleton(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        // Таблиці даних (F6-11): сховище для core.dataTable з лімітами OPENBP_DATA_TABLE_*.
        services.AddSingleton(_ => DataTableLimits.FromEnvironment());
        services.AddSingleton(sp => new PostgresDataTableStore(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<DataTableLimits>(),
            sp.GetRequiredService<TimeProvider>()));
        // Підсценарії (F6-04): core.executeWorkflow ставить дочірні виконання в ту саму чергу.
        services.AddSingleton(sp => new PostgresSubWorkflowLauncher(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<TimeProvider>()));
        // Відповіді webhook-ів (F6-05): core.respondToWebhook і respond: whenFinished пишуть у webhook_responses.
        services.AddSingleton(sp => new PostgresWebhookResponder(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<INodeRegistry>(sp => new WorkerNodeRegistry(
            CoreNodes.All(
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<PostgresDataTableStore>(),
                sp.GetRequiredService<PostgresSubWorkflowLauncher>(),
                sp.GetRequiredService<PostgresWebhookResponder>())
                .Concat(McpNodes.All(sp.GetRequiredService<IMcpServerRegistry>(), sp.GetRequiredService<TimeProvider>()))
                .Concat(AiNodes.All(
                    sp.GetRequiredService<HttpClient>(),
                    sp.GetRequiredService<IMcpServerRegistry>(),
                    sp.GetRequiredService<PostgresDataTableStore>()))));
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
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<WorkflowLimits>(),
            sp.GetRequiredService<WorkerMetrics>(),
            sp.GetRequiredService<PostgresWebhookResponder>()));
        services.AddSingleton(sp => new PollJobProcessor(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<CredentialStore>(),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<McpServerSync>(),
            sp.GetRequiredService<IExpressionEvaluator>(),
            sp.GetService<IBinaryStore>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PollJobProcessor>>(),
            sp.GetRequiredService<TimeProvider>()));
        // Пояснення сценаріїв (F3-72): модель помічника з «Налаштувань → Помічник».
        services.AddSingleton(sp => new WorkflowExplainer(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<CredentialEncryptionKey>(),
            new LlmChat(new HttpClient { Timeout = TimeSpan.FromMinutes(4) }),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ExplainJobProcessor(
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<WorkflowExplainer>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExplainJobProcessor>>()));
        services.AddHostedService(sp => new ExecutionWorker(
            sp.GetRequiredService<JobQueue>(),
            sp.GetRequiredService<ExecutionJobProcessor>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExecutionWorker>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<PollJobProcessor>(),
            sp.GetRequiredService<WorkerLiveness>(),
            sp.GetRequiredService<ExplainJobProcessor>()));
        // Підписки push-тригерів mcp.subscribe (F6-13).
        services.AddHostedService(sp => new SubscriptionService(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<IMcpServerRegistry>(),
            sp.GetRequiredService<McpServerSync>(),
            sp.GetRequiredService<PollJobProcessor>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SubscriptionService>>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddHostedService(sp => new WorkerHeartbeatService(
            sp.GetRequiredService<IDbContextFactory<OpenBpDbContext>>(),
            sp.GetRequiredService<WorkerLiveness>(),
            sp.GetRequiredService<WorkerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkerHeartbeatService>>(),
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
