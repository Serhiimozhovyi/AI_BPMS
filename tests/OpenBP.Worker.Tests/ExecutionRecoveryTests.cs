using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Worker.Tests;

/// <summary>Відновлення після падіння воркера (F2-08, docs/architecture.md §2, §10.4).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ExecutionRecoveryTests(PostgresFixture postgres)
{
    private const string KilledWorkerId = "killed-worker";

    /// <summary>
    /// DoD F2-08: окремий процес воркера вбито посеред сценарію (після збереженого кроку <c>n_token</c>, під час
    /// <c>n_wait</c>) → новий воркер через відновлення доводить виконання до <c>success</c>; підсумок такий самий, як у
    /// виконання без падіння, а <c>n_token</c> (випадковий токен) не виконано вдруге.
    /// </summary>
    [Fact]
    public async Task KilledWorker_NewWorkerFinishesFromLastSavedStep_NoNodeRunTwice()
    {
        // Поріг із запасом: lock подовжується лише з першим heartbeat, а до нього воркер готує виконання (на холодному
        // CI це може тривати понад секунду) — менший поріг дав би службі відновлення забрати job у живого воркера.
        await using var worker = await WorkerHarness.CreateAsync(postgres, lockTimeout: TimeSpan.FromSeconds(5));
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.RandomTokenThenWait(waitMs: 3_000));
        var executionId = await worker.LaunchAsync(workflowId, versionId);

        // 1. Окремий процес воркера бере job і доходить до паузи; вбиваємо його без жодного шансу прибрати за собою.
        string tokenBeforeKill;
        using (var killed = StartWorkerProcess(worker.ConnectionString))
        {
            try
            {
                await WaitForStepAsync(worker, executionId, "n_token", killed);
                tokenBeforeKill = Token(await worker.StepsAsync(executionId), "n_token");
            }
            finally
            {
                killed.Kill(entireProcessTree: true);
                await killed.WaitForExitAsync();
            }
        }

        var stepsBeforeKill = await worker.StepsAsync(executionId);
        Assert.Equal(["n_start", "n_token"], stepsBeforeKill.Select(s => s.NodeId));
        Assert.Equal(ExecutionStatuses.Running, (await worker.ExecutionAsync(executionId)).Status);
        await using (var db = worker.Db.CreateDbContext())
        {
            Assert.Equal(KilledWorkerId, (await db.Jobs.AsNoTracking().SingleAsync()).LockedBy);
        }

        // 2. Новий воркер: відновлення повертає протухлий job у чергу, воркер продовжує зі знімка.
        await worker.StartAsync(withRecovery: true);
        var recovered = await worker.WaitForFinalAsync(executionId);

        Assert.True(recovered.Status == ExecutionStatuses.Success, recovered.Error);
        var steps = await worker.StepsAsync(executionId);
        Assert.Equal(["n_start", "n_token", "n_wait", "n_set"], steps.Select(s => s.NodeId));
        Assert.All(steps, s => Assert.Equal("success", s.Status));
        Assert.Equal(stepsBeforeKill.Select(s => s.Id), steps.Take(2).Select(s => s.Id));
        var final = FinalItem(steps);
        Assert.Equal(tokenBeforeKill, final["token"]!.ToJsonString());
        Assert.Equal(0, await worker.JobCountAsync());

        // 3. Те саме виконання без падіння дає ті самі кроки й підсумок тієї самої форми.
        var cleanId = await worker.LaunchAsync(workflowId, versionId);
        var clean = await worker.WaitForFinalAsync(cleanId);
        Assert.Equal(ExecutionStatuses.Success, clean.Status);
        var cleanSteps = await worker.StepsAsync(cleanId);
        Assert.Equal(cleanSteps.Select(s => (s.NodeId, s.RunIndex, s.Status)), steps.Select(s => (s.NodeId, s.RunIndex, s.Status)));
        var cleanFinal = FinalItem(cleanSteps);
        Assert.Equal(cleanFinal.Select(p => p.Key).Order(), final.Select(p => p.Key).Order());
        Assert.Equal(cleanFinal["done"]!.ToJsonString(), final["done"]!.ToJsonString());
    }

    [Fact]
    public async Task Recover_ReleasesStaleLock_AndFailsOnlyOrphanedStaleExecutions()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, lockTimeout: TimeSpan.FromSeconds(30));
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 1));
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);

        // Протухлий lock упалого воркера: job повертається, виконання лишається running для resume.
        var withStaleJob = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        // Job вилучено після MaxAttempts, heartbeat давно не оновлювався: продовжити нікому → error.
        var orphaned = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        // Без job'а, але heartbeat свіжий: не чіпаємо.
        var fresh = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        // Без job'а і без heartbeat, але вже фінальне: не чіпаємо.
        var finished = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Success);
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Executions.ExecuteUpdateAsync(s => s.SetProperty(e => e.HeartbeatAt, stale));
            await db.Executions.Where(e => e.Id == fresh).ExecuteUpdateAsync(s => s.SetProperty(e => e.HeartbeatAt, DateTimeOffset.UtcNow));
            var jobs = await db.Jobs.ToListAsync();
            foreach (var job in jobs)
            {
                var id = Guid.Parse(JsonNode.Parse(job.Payload)!["executionId"]!.GetValue<string>());
                if (id == withStaleJob)
                {
                    job.LockedBy = KilledWorkerId;
                    job.LockedAt = stale;
                }
                else
                {
                    db.Jobs.Remove(job);
                }
            }

            await db.SaveChangesAsync();
        }

        var result = await worker.Recovery.RecoverAsync();

        Assert.Equal(new RecoveryResult(ReleasedJobs: 1, OrphanedExecutions: 1), result);
        Assert.Equal(ExecutionStatuses.Running, (await worker.ExecutionAsync(withStaleJob)).Status);
        var failed = await worker.ExecutionAsync(orphaned);
        Assert.Equal(ExecutionStatuses.Error, failed.Status);
        Assert.Equal(ExecutionRecoveryService.OrphanedError, failed.Error);
        Assert.NotNull(failed.FinishedAt);
        Assert.Equal(ExecutionStatuses.Running, (await worker.ExecutionAsync(fresh)).Status);
        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(finished)).Status);
        await using (var db = worker.Db.CreateDbContext())
        {
            var job = await db.Jobs.AsNoTracking().SingleAsync();
            Assert.Null(job.LockedBy);
            Assert.Equal(1, job.Attempts);
        }

        // Другий прохід нічого не змінює; повернутий job відновлюється до кінця.
        Assert.Equal(new RecoveryResult(0, 0), await worker.Recovery.RecoverAsync());
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(ExecutionStatuses.Success, (await worker.ExecutionAsync(withStaleJob)).Status);
    }

    [Fact]
    public async Task RecoveryService_RunsAtStartup()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres, lockTimeout: TimeSpan.FromMinutes(10));
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(Workflows.Waiting(waitMs: 1));
        var executionId = await worker.LaunchAsync(workflowId, versionId, status: ExecutionStatuses.Running);
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Jobs.ExecuteDeleteAsync();
            await db.Executions.ExecuteUpdateAsync(s => s.SetProperty(e => e.HeartbeatAt, DateTimeOffset.UtcNow - TimeSpan.FromHours(1)));
        }

        // Період — 5 хв, тож error за секунди означає прохід при старті.
        await worker.StartAsync(withRecovery: true);
        var execution = await worker.WaitForFinalAsync(executionId, TimeSpan.FromSeconds(10));

        Assert.Equal(ExecutionStatuses.Error, execution.Status);
        Assert.Equal(ExecutionRecoveryService.OrphanedError, execution.Error);
    }

    [Fact]
    public void AddOpenBpWorker_BindsQueueOptions_AndRejectsHeartbeatNotBelowLockTimeout()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddOpenBpWorker(Config(("Worker:Queue:LockTimeout", "00:00:30"), ("Worker:Queue:MaxAttempts", "3")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<JobQueueOptions>();
        Assert.Equal(TimeSpan.FromSeconds(30), options.LockTimeout);
        Assert.Equal(3, options.MaxAttempts);

        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddOpenBpWorker(
            Config(("Worker:HeartbeatInterval", "00:00:30"), ("Worker:Queue:LockTimeout", "00:00:30"))));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    /// <summary>Справжній хост <c>OpenBP.Worker</c> окремим процесом над БД тесту.</summary>
    private static Process StartWorkerProcess(string connectionString)
    {
        var start = new ProcessStartInfo(Workflows.DotNet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OpenBP.Worker.dll"));
        start.Environment["ConnectionStrings__Default"] = connectionString;
        start.Environment[CredentialEncryptionKey.EnvironmentVariable] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        start.Environment["Worker__WorkerId"] = KilledWorkerId;
        start.Environment["Worker__HeartbeatInterval"] = "00:00:00.200";
        start.Environment["Worker__PollInterval"] = "00:00:00.100";
        // Власна служба відновлення процесу не повинна забрати job у нього самого, поки він живий.
        start.Environment["Worker__Queue__LockTimeout"] = "00:05:00";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Не вдалося запустити процес воркера.");

        // Читаємо виводи, щоб процес не заблокувався на повному буфері.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForStepAsync(WorkerHarness worker, Guid executionId, string nodeId, Process process)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!(await worker.StepsAsync(executionId)).Any(s => s.NodeId == nodeId))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Процес воркера завершився з кодом {process.ExitCode} до кроку {nodeId}.");
            }

            await Task.Delay(50, cts.Token);
        }
    }

    private static string Token(List<ExecutionStepEntity> steps, string nodeId) =>
        JsonNode.Parse(steps.Single(s => s.NodeId == nodeId).Output!)![0]![0]!["json"]!["token"]!.ToJsonString();

    private static JsonObject FinalItem(List<ExecutionStepEntity> steps) =>
        JsonNode.Parse(steps[^1].Output!)![0]![0]!["json"]!.AsObject();
}
