using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.FakeServer;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F6-13: push-тригер <c>mcp.subscribe</c> проти HTTP FakeServer з ресурсом <c>fake://events</c>: воркер тримає підписку,
/// подія → виконання за ≤ 1 с, дублікат події нічого не запускає, падіння воркера → підписку підхоплює інший.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SubscriptionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WebApplication _app = null!;
    private Uri _url = null!;

    public async Task InitializeAsync()
    {
        _app = FakeServerApp.BuildHttp("http://127.0.0.1:0");
        await _app.StartAsync();
        _url = new Uri(new Uri(_app.Urls.First()), "/mcp");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Event_launches_execution_within_a_second_and_duplicates_are_ignored()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker);
        var (workflowId, _) = await worker.SaveWorkflowAsync(EventsWorkflow(server), active: true);

        var scan = await worker.Subscriptions.ScanAsync();
        Assert.Equal(new SubscriptionScan(1, 1, 1, 0), scan);
        Assert.Equal(0, await ExecutionCountAsync(worker, workflowId));

        var watch = Stopwatch.StartNew();
        await EmitAsync(worker, server, "ev1", new JsonObject { ["text"] = "перша" });
        var executionId = await WaitForExecutionsAsync(worker, workflowId, 1);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"подія дійшла за {watch.Elapsed.TotalMilliseconds:0} мс");
        Assert.Equal(["ev1"], await StartIdsAsync(worker, executionId));

        // Та сама подія ще раз — ключ уже бачено, виконання немає; нова подія — лише вона.
        await EmitAsync(worker, server, "ev1", null);
        await Task.Delay(300);
        Assert.Equal(1, await ExecutionCountAsync(worker, workflowId));
        await EmitAsync(worker, server, "ev2", null);
        var second = await WaitForExecutionsAsync(worker, workflowId, 2);
        Assert.Equal(["ev2"], await StartIdsAsync(worker, second));

        // Виконання стартує з події; наступний вузол бачить її payload.
        await worker.StartAsync();
        var done = await worker.WaitForFinalAsync(executionId);
        Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
        var mark = (await worker.StepsAsync(executionId)).Single(s => s.NodeId == "n_mark");
        var item = JsonNode.Parse(mark.Output!)!.AsArray()[0]!.AsArray().Single()!;
        Assert.Equal("перша", item["json"]!["payload"]!["text"]!.GetValue<string>());
        await worker.StopAsync();

        // Деактивація: рядок зникає, сесія закривається.
        await using (var db = worker.Db.CreateDbContext())
        {
            await db.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(s => s.SetProperty(w => w.Active, false));
        }

        Assert.Equal(new SubscriptionScan(0, 0, 0, 1), await worker.Subscriptions.ScanAsync());
        Assert.Empty(worker.Subscriptions.HeldSubscriptions);
        await using (var db = worker.Db.CreateDbContext())
        {
            Assert.False(await db.Subscriptions.AnyAsync());
        }
    }

    [Fact]
    public async Task Catch_up_on_open_delivers_events_missed_while_unsubscribed()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker);
        await EmitAsync(worker, server, "old1", null);
        await EmitAsync(worker, server, "old2", null);
        var (workflowId, _) = await worker.SaveWorkflowAsync(EventsWorkflow(server), active: true);

        await worker.Subscriptions.ScanAsync();

        var executionId = await WaitForExecutionsAsync(worker, workflowId, 1);
        Assert.Equal(["old1", "old2"], await StartIdsAsync(worker, executionId));
    }

    [Fact]
    public async Task Crashed_worker_keeps_lease_until_it_expires_then_another_worker_takes_over()
    {
        var lease = TimeSpan.FromSeconds(2);
        await using var first = await WorkerHarness.CreateAsync(postgres, subscriptionLease: lease);
        await using var second = first.SecondWorker(binary: null);
        var server = await AddServerAsync(first);
        var (workflowId, _) = await first.SaveWorkflowAsync(EventsWorkflow(server), active: true);

        Assert.Equal(1, (await first.Subscriptions.ScanAsync()).Held);
        Assert.Equal(0, (await second.Subscriptions.ScanAsync()).Held);
        Assert.Equal(first.Options.WorkerId, await LeaseOwnerAsync(first, workflowId));

        // Перший воркер «упав»: сесію втрачено, оренда лишилась.
        await first.Subscriptions.DropAsync();
        Assert.Empty(first.Subscriptions.HeldSubscriptions);
        Assert.Equal(0, (await second.Subscriptions.ScanAsync()).Held);
        await EmitAsync(first, server, "lost", null);
        await Task.Delay(300);
        Assert.Equal(0, await ExecutionCountAsync(first, workflowId));

        // Після строку оренди підписку бере другий воркер і надолужує пропущену подію.
        await Task.Delay(lease + TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, (await second.Subscriptions.ScanAsync()).Held);
        Assert.Equal(second.Options.WorkerId, await LeaseOwnerAsync(first, workflowId));
        var caughtUp = await WaitForExecutionsAsync(first, workflowId, 1);
        Assert.Equal(["lost"], await StartIdsAsync(first, caughtUp));

        // Нова подія доходить через другого воркера; перший, прокинувшись, оренду не відбирає.
        Assert.Equal(0, (await first.Subscriptions.ScanAsync()).Held);
        await EmitAsync(first, server, "after", null);
        var next = await WaitForExecutionsAsync(first, workflowId, 2);
        Assert.Equal(["after"], await StartIdsAsync(first, next));

        // Штатна зупинка другого воркера звільняє оренду одразу.
        await second.Subscriptions.ReleaseAsync();
        Assert.Null(await LeaseOwnerAsync(first, workflowId));
        Assert.Equal(1, (await first.Subscriptions.ScanAsync()).Held);
    }

    [Fact]
    public async Task Unreachable_connector_is_retried_with_backoff_without_losing_the_lease()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var broken = Workflows.HttpServer("Зламаний", new Uri("http://127.0.0.1:1/mcp"));
        await using (var db = worker.Db.CreateDbContext())
        {
            db.McpServers.Add(broken);
            await db.SaveChangesAsync();
        }

        var (workflowId, _) = await worker.SaveWorkflowAsync(EventsWorkflow(broken.Id), active: true);

        Assert.Equal(new SubscriptionScan(1, 0, 0, 0), await worker.Subscriptions.ScanAsync());
        Assert.Equal(worker.Options.WorkerId, await LeaseOwnerAsync(worker, workflowId));
        // Друга спроба відкладена (backoff): прохід нічого не відкриває і не падає.
        Assert.Equal(new SubscriptionScan(1, 0, 0, 0), await worker.Subscriptions.ScanAsync());
    }

    // ---------- допоміжне ----------

    /// <summary><c>mcp.subscribe</c> на <c>fake://events</c> (записи у <c>result</c>) → <c>core.set</c>.</summary>
    private static WorkflowDefinition EventsWorkflow(Guid serverId) => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "Події конектора",
          "settings": { "timezone": "UTC", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_sub", "name": "Подія", "type": "mcp.subscribe", "typeVersion": 1, "position": [0, 0],
              "parameters": { "serverId": "{{serverId}}", "resourceUri": "{{FakeEvents.Uri}}", "itemsPath": "result", "keyField": "id" } },
            { "id": "n_mark", "name": "Позначка", "type": "core.set", "typeVersion": 1, "position": [200, 0],
              "parameters": { "fields": [ { "name": "seen", "value": "так" } ] } }
          ],
          "connections": [ { "from": { "node": "n_sub" }, "to": { "node": "n_mark" } } ]
        }
        """);

    private async Task<Guid> AddServerAsync(WorkerHarness worker)
    {
        var server = Workflows.HttpServer("Події (мок)", _url);
        await using var db = worker.Db.CreateDbContext();
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server.Id;
    }

    /// <summary>Подія «ззовні»: tool <c>emit_event</c> через пул реєстру воркера (окрема сесія від підписки).</summary>
    private async Task EmitAsync(WorkerHarness worker, Guid serverId, string id, JsonObject? payload)
    {
        if (!worker.Mcp.TryGetServer(serverId.ToString(), out _))
        {
            await worker.Mcp.RegisterAsync(new McpServerConfig { Id = serverId.ToString(), Name = "Події (мок)", Transport = McpTransportKind.Http, Url = _url });
        }

        var args = new Dictionary<string, object?> { ["id"] = id };
        if (payload is not null)
        {
            args["payload"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(payload.ToJsonString());
        }

        await worker.Mcp.RunAsync(serverId.ToString(), (c, ct) => c.CallToolAsync("emit_event", args, cancellationToken: ct).AsTask());
    }

    private static async Task<Guid> WaitForExecutionsAsync(WorkerHarness worker, Guid workflowId, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var db = worker.Db.CreateDbContext();
            var executions = await db.Executions.AsNoTracking().Where(e => e.WorkflowId == workflowId).OrderBy(e => e.Id).ToListAsync();
            if (executions.Count >= count)
            {
                Assert.Equal(count, executions.Count);
                return executions[^1].Id;
            }

            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Очікувалось {count} виконань, є {executions.Count}.");
            }

            await Task.Delay(20);
        }
    }

    private static async Task<int> ExecutionCountAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Executions.CountAsync(e => e.WorkflowId == workflowId);
    }

    private static async Task<string?> LeaseOwnerAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Subscriptions.AsNoTracking().Where(s => s.WorkflowId == workflowId).Select(s => s.WorkerId).SingleAsync();
    }

    private static async Task<string[]> StartIdsAsync(WorkerHarness worker, Guid executionId)
    {
        await using var db = worker.Db.CreateDbContext();
        var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Single(p => p.ExecutionId == executionId);
        Assert.Equal(ExecutionMode.Trigger, payload.Mode);
        Assert.Equal("n_sub", payload.StartNodeId);
        return [.. payload.StartItems!.Select(i => i.Json["id"]!.GetValue<string>())];
    }
}
