using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenBP.Ai;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Nodes;
using OpenBP.Nodes.Core;
using OpenBP.Shared.Workflow;
using Testcontainers.PostgreSql;

namespace OpenBP.Worker.Tests;

/// <summary>Піднімає <c>postgres:16</c> у Testcontainers один раз на колекцію тестів.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Нова БД з міграціями. Кожен тест — у своїй БД: воркер бере будь-який job черги, тож спільна БД змішала б тести.
    /// </summary>
    public async Task<string> NewMigratedDatabaseAsync()
    {
        var name = "w_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
        await using var db = new OpenBpDbContext(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(cs).Options);
        await db.Database.MigrateAsync();
        return cs;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>Фабрика контекстів над рядком підключення.</summary>
internal sealed class Contexts(string cs) : IDbContextFactory<OpenBpDbContext>
{
    public OpenBpDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(cs).Options);
}

/// <summary>
/// Воркер над окремою БД: ті самі класи, що в хості (<see cref="ExecutionWorker"/>, <see cref="ExecutionJobProcessor"/>),
/// плюс помічники для підготовки сценаріїв, виконань і job'ів так, як це роблять <c>IExecutionLauncher</c> і
/// <c>IExecutionService</c> (docs/architecture.md §10.4).
/// </summary>
internal sealed class WorkerHarness : IAsyncDisposable
{
    private readonly McpServerRegistry _mcp = new();
    private readonly HttpClient _http = new();
    private ExecutionWorker? _worker;

    private WorkerHarness(string cs, WorkerOptions options)
    {
        Db = new Contexts(cs);
        Options = options;
        Queue = new JobQueue(Db);
        Credentials = new CredentialStore(Db, CredentialEncryptionKey.FromBase64(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Servers = new McpServerSync(Db, _mcp, Credentials);
        var nodes = CoreNodes.All().Concat(McpNodes.All(_mcp)).Concat(AiNodes.All(_http)).ToList();

        // core.webhook з'явиться в F2-10; до того прикладу telegram-1c.json потрібна заглушка (він запускається з pinData).
        if (!nodes.Any(n => n.Descriptor.Type == WebhookStub.Type))
        {
            nodes.Add(new WebhookStub());
        }

        Processor = new ExecutionJobProcessor(
            Db,
            Queue,
            Credentials,
            new WorkerNodeRegistry(nodes),
            Servers,
            new JintExpressionEvaluator(),
            null,
            options,
            NullLogger<ExecutionJobProcessor>.Instance);
    }

    public Contexts Db { get; }

    public WorkerOptions Options { get; }

    public JobQueue Queue { get; }

    public CredentialStore Credentials { get; }

    public McpServerSync Servers { get; }

    public IMcpServerRegistry Mcp => _mcp;

    public ExecutionJobProcessor Processor { get; }

    public static async Task<WorkerHarness> CreateAsync(PostgresFixture postgres, int maxConcurrent = 4)
    {
        var cs = await postgres.NewMigratedDatabaseAsync();
        return new WorkerHarness(cs, new WorkerOptions
        {
            WorkerId = "test-worker",
            MaxConcurrentExecutions = maxConcurrent,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            PollInterval = TimeSpan.FromMilliseconds(50),
        });
    }

    /// <summary>Запускає <see cref="ExecutionWorker"/> як hosted service.</summary>
    public async Task StartAsync()
    {
        _worker = new ExecutionWorker(Queue, Processor, Options, NullLogger<ExecutionWorker>.Instance);
        await _worker.StartAsync(CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync(CancellationToken.None);
            _worker.Dispose();
            _worker = null;
        }
    }

    /// <summary>Сценарій з однією версією; повертає (workflowId, versionId).</summary>
    public async Task<(Guid WorkflowId, Guid VersionId)> SaveWorkflowAsync(WorkflowDefinition definition)
    {
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await using var db = Db.CreateDbContext();
        db.Workflows.Add(new WorkflowEntity { Id = workflowId, Name = definition.Name, CurrentVersionId = versionId, UpdatedAt = DateTimeOffset.UtcNow });
        db.WorkflowVersions.Add(new WorkflowVersionEntity
        {
            Id = versionId,
            WorkflowId = workflowId,
            Version = 1,
            Definition = WorkflowJson.Serialize(definition),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (workflowId, versionId);
    }

    /// <summary>Як <c>IExecutionLauncher</c>: рядок <c>executions</c> у стані <c>queued</c> і job <c>execution</c>.</summary>
    public async Task<Guid> LaunchAsync(
        Guid workflowId,
        Guid versionId,
        ExecutionMode mode = ExecutionMode.Manual,
        string status = ExecutionStatuses.Queued,
        string? startNodeId = null,
        IReadOnlyList<Item>? startItems = null)
    {
        var executionId = Guid.NewGuid();
        await using (var db = Db.CreateDbContext())
        {
            db.Executions.Add(new ExecutionEntity
            {
                Id = executionId,
                WorkflowId = workflowId,
                VersionId = versionId,
                Mode = mode.ToString().ToLowerInvariant(),
                Status = status,
                StartedAt = status == ExecutionStatuses.Queued ? null : DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await Queue.EnqueueAsync(JobKinds.Execution, JobJson.Serialize(new ExecutionJobPayload
        {
            ExecutionId = executionId,
            WorkflowId = workflowId,
            VersionId = versionId,
            Mode = mode,
            StartNodeId = startNodeId,
            StartItems = startItems,
        }));
        return executionId;
    }

    /// <summary>Як <c>IExecutionService.CancelAsync</c> для виконання, що вже йде: <c>running</c> → <c>cancelling</c>.</summary>
    public async Task RequestCancelAsync(Guid executionId)
    {
        await using var db = Db.CreateDbContext();
        await db.Executions
            .Where(e => e.Id == executionId && e.Status == ExecutionStatuses.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatuses.Cancelling));
    }

    public async Task<ExecutionEntity> ExecutionAsync(Guid executionId)
    {
        await using var db = Db.CreateDbContext();
        return await db.Executions.AsNoTracking().SingleAsync(e => e.Id == executionId);
    }

    public async Task<List<ExecutionStepEntity>> StepsAsync(Guid executionId)
    {
        await using var db = Db.CreateDbContext();
        return await db.ExecutionSteps.AsNoTracking().Where(s => s.ExecutionId == executionId).OrderBy(s => s.Id).ToListAsync();
    }

    public async Task<int> JobCountAsync()
    {
        await using var db = Db.CreateDbContext();
        return await db.Jobs.CountAsync();
    }

    /// <summary>Чекає, поки виконання не перейде в стан, що задовольняє умову.</summary>
    public async Task<ExecutionEntity> WaitForAsync(Guid executionId, Func<ExecutionEntity, bool> condition, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var execution = await ExecutionAsync(executionId);
            if (condition(execution))
            {
                return execution;
            }

            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Виконання {executionId} лишилось у стані «{execution.Status}».");
            }

            await Task.Delay(50);
        }
    }

    public Task<ExecutionEntity> WaitForFinalAsync(Guid executionId, TimeSpan? timeout = null) =>
        WaitForAsync(
            executionId,
            e => e.Status is ExecutionStatuses.Success or ExecutionStatuses.Error or ExecutionStatuses.Cancelled or ExecutionStatuses.TimedOut,
            timeout);

    /// <summary>Бере job і обробляє його синхронно — без <see cref="ExecutionWorker"/>.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken stoppingToken = default)
    {
        var job = await Queue.ClaimAsync(Options.WorkerId, [JobKinds.Execution]);
        if (job is null)
        {
            return false;
        }

        await Processor.ProcessAsync(job, stoppingToken);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _mcp.DisposeAsync();
        _http.Dispose();
    }

    /// <summary>Заглушка тригера <c>core.webhook</c> до F2-10: працює лише з pinData/startItems.</summary>
    private sealed class WebhookStub : INode
    {
        public const string Type = "core.webhook";

        public NodeDescriptor Descriptor { get; } = new(Type, 1, "Webhook (заглушка тесту)", NodeGroup.Trigger, [], ["main"], []);

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("Заглушка core.webhook: закріпіть вихід у pinData.");
    }
}

/// <summary>Сценарії для тестів.</summary>
internal static class Workflows
{
    /// <summary><c>core.manualTrigger</c> → <c>core.wait</c> на <paramref name="waitMs"/> → <c>core.set</c>.</summary>
    public static WorkflowDefinition Waiting(int waitMs) => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "Очікування",
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_wait", "name": "Пауза", "type": "core.wait", "typeVersion": 1, "position": [200, 0], "parameters": { "amount": {{waitMs}}, "unit": "milliseconds" } },
            { "id": "n_set", "name": "Готово", "type": "core.set", "typeVersion": 1, "position": [400, 0], "parameters": { "fields": [ { "name": "done", "value": "так" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_wait" } },
            { "from": { "node": "n_wait" }, "to": { "node": "n_set" } }
          ]
        }
        """);

    /// <summary>Приклад <c>examples/telegram-1c.json</c>, у якому <c>serverId</c> замінено на id рядків <c>mcp_servers</c>.</summary>
    public static WorkflowDefinition Telegram1C(Guid oneCServerId, Guid telegramServerId)
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot, "examples", "telegram-1c.json"))
            .Replace("\"srv_1c\"", $"\"{oneCServerId}\"", StringComparison.Ordinal)
            .Replace("\"srv_telegram\"", $"\"{telegramServerId}\"", StringComparison.Ordinal);
        return WorkflowJson.Deserialize(json);
    }

    /// <summary>Рядок <c>mcp_servers</c> для stdio-моку FakeServer (<c>--mock 1c|telegram</c>, без моку — загальні tools).</summary>
    public static McpServerEntity FakeServer(string name, string? mock = null, Guid? credentialId = null)
    {
        var args = new JsonArray(Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll"));
        if (mock is not null)
        {
            args.Add("--mock");
            args.Add(mock);
        }

        return new McpServerEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Transport = "stdio",
            Config = new JsonObject { ["command"] = DotNet, ["arguments"] = args }.ToJsonString(),
            CredentialId = credentialId,
            Status = "new",
        };
    }

    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static string RepoRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OpenBP.sln")))
                {
                    return dir.FullName;
                }
            }

            throw new InvalidOperationException("Не знайдено корінь репозиторію (OpenBP.sln).");
        }
    }
}
