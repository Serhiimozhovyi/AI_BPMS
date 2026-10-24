using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBP.Data;

namespace OpenBP.Worker.Tests;

/// <summary>F6-07: метрики воркера (<c>/metrics</c>) і heartbeat у <c>worker_heartbeats</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WorkerMetricsTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Heartbeat_InsertsThenUpdatesOneRowPerWorker()
    {
        var cs = await postgres.NewMigratedDatabaseAsync();
        var options = new WorkerOptions { WorkerId = "w-heartbeat" };
        var service = new WorkerHeartbeatService(new Contexts(cs), new WorkerLiveness(), options, NullLogger<WorkerHeartbeatService>.Instance, TimeProvider.System);

        await service.BeatAsync(default);
        await using var db = new Contexts(cs).CreateDbContext();
        var first = await db.WorkerHeartbeats.AsNoTracking().SingleAsync();
        await service.BeatAsync(default);
        var second = await db.WorkerHeartbeats.AsNoTracking().SingleAsync();

        Assert.Equal("w-heartbeat", second.WorkerId);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.True(second.SeenAt >= first.SeenAt);
    }

    [Fact]
    public void Metrics_CountFinishedAndRunningExecutions()
    {
        var metrics = new WorkerMetrics();
        using (metrics.Running())
        {
            Assert.Equal(1, metrics.RunningCount);
            Assert.Contains("openbp_worker_running_executions{worker=\"w1\"} 1", metrics.Render("w1"), StringComparison.Ordinal);
        }

        metrics.Record(ExecutionStatuses.Success, TimeSpan.FromMilliseconds(80));
        metrics.Record(ExecutionStatuses.Error, TimeSpan.FromSeconds(3));
        var text = metrics.Render("w1");

        Assert.Equal(0, metrics.RunningCount);
        Assert.Contains("openbp_worker_executions_total{worker=\"w1\",status=\"success\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("openbp_worker_executions_total{worker=\"w1\",status=\"error\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("openbp_worker_execution_duration_seconds_count 2", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("true", HttpStatusCode.OK)]
    public async Task MetricsEndpoint_NeedsApiKeyUnlessAnonymousAllowed(string? anonymous, HttpStatusCode expected)
    {
        var cs = await postgres.NewMigratedDatabaseAsync();
        Environment.SetEnvironmentVariable("OPENBP_ENCRYPTION_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        await using var app = WorkerHost.Build([], b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = cs,
            [WorkerHost.HealthPortKey] = "0",
            ["OPENBP_METRICS_ALLOW_ANONYMOUS"] = anonymous,
        }));
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };

        var response = await http.GetAsync("/metrics");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Contains("openbp_worker_running_executions", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await app.StopAsync();
    }
}
