using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenBP.Data;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>F4-08: health-ендпойнти воркера і scope журналу виконань.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerHealthTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Ready_IsGreenWithDbAndLoop_RedWhenLoopStops()
    {
        var cs = await postgres.NewMigratedDatabaseAsync();
        Environment.SetEnvironmentVariable(CredentialEncryptionKeyVariable, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await using var app = WorkerHost.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = cs,
            [WorkerHost.HealthPortKey] = "0",
            ["Worker:PollInterval"] = "00:00:00.100",
            ["Worker:HeartbeatInterval"] = "00:00:00.100",
            ["Worker:HealthMaxPollAge"] = "00:00:01",
        }));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);
        await WaitForStatusAsync(http, HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/workflows")).StatusCode);

        // Цикл опитування зупинено (процес живий): ready червоніє, live — ні.
        await app.Services.GetServices<IHostedService>().OfType<ExecutionWorker>().Single().StopAsync(default);
        await WaitForStatusAsync(http, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live")).StatusCode);

        await app.StopAsync();
    }

    [Theory]
    [InlineData("json", "json")]
    [InlineData(null, "simple")]
    public async Task LogFormat_JsonConsoleWithScopes_WhenRequested(string? format, string formatter)
    {
        await using var app = WorkerHost.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=127.0.0.1;Port=1",
            ["OPENBP_LOG_FORMAT"] = format,
            [WorkerHost.HealthPortKey] = "0",
        }));

        var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>>().CurrentValue;
        Assert.Equal(formatter, options.FormatterName ?? "simple");
        if (format == "json")
        {
            var json = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Logging.Console.JsonConsoleFormatterOptions>>().CurrentValue;
            Assert.True(json.IncludeScopes);
        }
    }

    [Fact]
    public async Task DatabaseReadiness_RedWithoutDb_GreenWithMigratedDb()
    {
        var unreachable = new Contexts("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=2");
        Assert.NotNull(await DatabaseReadiness.CheckAsync(unreachable, default));
        Assert.Null(await DatabaseReadiness.CheckAsync(new Contexts(await postgres.NewMigratedDatabaseAsync()), default));
    }

    [Fact]
    public async Task ExecutionLog_HasExecutionScope_AndNoCredentialValues()
    {
        var capture = new CaptureProvider();
        using var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(capture));
        await using var worker = await WorkerHarness.CreateAsync(postgres, loggers: loggers);
        const string secret = "Bearer log-secret-token-42";
        var credentialId = await worker.Credentials.CreateAsync(
            "token", "mcp", new JsonObject { ["headers"] = new JsonObject { ["Authorization"] = secret } }, null, default);
        var server = new McpServerEntity
        {
            Id = Guid.NewGuid(),
            Name = "CRM",
            Transport = "http",
            Config = new JsonObject { ["url"] = "http://127.0.0.1:1/mcp" }.ToJsonString(),
            CredentialId = credentialId,
            Status = "new",
        };
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Add(server);
            await db.SaveChangesAsync();
        }

        // Вузол коду падає з повідомленням, що містить значення credential'а: у журнал воно не має потрапити.
        var workflow = WorkflowJson.Deserialize($$"""
            {
              "formatVersion": 1,
              "name": "Журнал",
              "nodes": [
                { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "n_code", "name": "Код", "type": "core.code", "typeVersion": 1, "position": [200, 0], "parameters": { "code": "throw new Error('{{secret}}');" } },
                { "id": "n_crm", "name": "CRM", "type": "mcp.tool", "typeVersion": 1, "position": [0, 200], "disabled": true, "parameters": { "serverId": "{{server.Id}}", "toolName": "echo" } }
              ],
              "connections": [ { "from": { "node": "n_start" }, "to": { "node": "n_code" } } ]
            }
            """);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(workflow);
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Error, (await worker.ExecutionAsync(executionId)).Status);

        var records = capture.Records.ToList();
        Assert.Contains(records, r => r.Message.Contains("n_code", StringComparison.Ordinal));
        Assert.All(records.Where(r => r.Message.Contains(executionId.ToString(), StringComparison.Ordinal)), r =>
        {
            Assert.Equal(executionId, r.Scope["executionId"]);
            Assert.Equal(workflowId, r.Scope["workflowId"]);
        });
        Assert.DoesNotContain(records, r => r.Message.Contains("log-secret-token", StringComparison.Ordinal));
    }

    private const string CredentialEncryptionKeyVariable = "OPENBP_ENCRYPTION_KEY";

    private static async Task WaitForStatusAsync(HttpClient http, HttpStatusCode expected)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HttpStatusCode last;
        do
        {
            last = (await http.GetAsync("/health/ready")).StatusCode;
            if (last == expected)
            {
                return;
            }

            await Task.Delay(100);
        }
        while (!cts.IsCancellationRequested);

        Assert.Fail($"/health/ready лишився {last}, очікувалось {expected}.");
    }

    private sealed record Record(string Message, IReadOnlyDictionary<string, object?> Scope);

    /// <summary>Провайдер журналу, що запам'ятовує повідомлення і пари scope — як їх бачить JSON-консоль.</summary>
    private sealed class CaptureProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<Record> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CaptureProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => owner._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scope = new Dictionary<string, object?>();
                owner._scopes.ForEachScope(
                    (s, acc) =>
                    {
                        if (s is IEnumerable<KeyValuePair<string, object>> pairs)
                        {
                            foreach (var (key, value) in pairs)
                            {
                                acc[key] = value;
                            }
                        }
                    },
                    scope);
                owner.Records.Enqueue(new Record(formatter(state, exception) + " " + exception, scope));
            }
        }
    }
}
