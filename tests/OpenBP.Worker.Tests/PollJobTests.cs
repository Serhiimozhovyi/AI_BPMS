using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Engine.Execution;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F2-12: job опитування <c>mcp.poll</c> (Quartz → <see cref="JobKinds.Poll"/> → воркер) проти FakeServer <c>orders</c>
/// з <c>itemsPath</c>: стан вузла в <c>node_state</c>, нові записи → виконання, немає нових → нічого.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PollJobTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_fires_second_gives_only_new_records_and_state_survives_restart()
    {
        var worker = await WorkerHarness.CreateAsync(postgres);
        try
        {
            var server = await AddServerAsync(worker);
            var (workflowId, versionId) = await worker.SaveWorkflowAsync(OrdersPoll(server, [1, 2]));
            await ActivateAsync(worker, workflowId);

            // Спрацювання 1: обидва записи нові.
            var first = await FireAsync(worker, workflowId);
            Assert.Equal(PollOutcome.Launched, first.Outcome);
            Assert.Equal(new[] { 1, 2 }, await StartIdsAsync(worker, first.ExecutionId!.Value));
            Assert.Equal(new[] { 1, 2 }, await SeenKeysAsync(worker, workflowId));

            // Джерело віддає ще й запис 3. Спрацювання 2: лише він.
            await SetIdsAsync(worker, versionId, server, [1, 2, 3]);
            var second = await FireAsync(worker, workflowId);
            Assert.Equal(PollOutcome.Launched, second.Outcome);
            Assert.Equal(new[] { 3 }, await StartIdsAsync(worker, second.ExecutionId!.Value));
            Assert.Equal(new[] { 1, 2, 3 }, await SeenKeysAsync(worker, workflowId));

            // Виконання стартує з записів опитування, вузол mcp.poll не виконується повторно, а стан не перезаписується.
            Assert.True(await worker.ProcessNextAsync());
            Assert.True(await worker.ProcessNextAsync());
            var done = await worker.WaitForFinalAsync(second.ExecutionId.Value);
            Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
            var mark = (await worker.StepsAsync(second.ExecutionId.Value)).Single(s => s.NodeId == "n_mark");
            var marked = JsonNode.Parse(mark.Output!)!.AsArray()[0]!.AsArray();
            Assert.Equal(3, Assert.Single(marked)!["json"]!["id"]!.GetValue<int>());
            Assert.Equal(new[] { 1, 2, 3 }, await SeenKeysAsync(worker, workflowId));

            // Перезапуск воркера: стан читається з БД, тож те саме джерело нових записів не дає.
            var restarted = await worker.RestartAsync();
            await worker.DisposeAsync();
            worker = restarted;
            var executions = await ExecutionCountAsync(worker, workflowId);

            var third = await FireAsync(worker, workflowId);
            Assert.Equal(PollOutcome.NoNewItems, third.Outcome);
            Assert.Equal(executions, await ExecutionCountAsync(worker, workflowId));

            await SetIdsAsync(worker, versionId, server, [1, 2, 3, 4]);
            var fourth = await FireAsync(worker, workflowId);
            Assert.Equal(PollOutcome.Launched, fourth.Outcome);
            Assert.Equal(new[] { 4 }, await StartIdsAsync(worker, fourth.ExecutionId!.Value));
            Assert.Equal(0, await PollJobCountAsync(worker));
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Worker_host_takes_poll_jobs_and_runs_the_launched_execution()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker);
        var (workflowId, _) = await worker.SaveWorkflowAsync(OrdersPoll(server, [5]));
        await ActivateAsync(worker, workflowId);
        await EnqueuePollAsync(worker, workflowId);

        await worker.StartAsync();
        var executionId = await WaitForExecutionAsync(worker, workflowId);
        var execution = await worker.WaitForFinalAsync(executionId);

        Assert.True(execution.Status == ExecutionStatuses.Success, execution.Error);
        Assert.Equal("trigger", execution.Mode);
        Assert.Equal(new[] { 5 }, await SeenKeysAsync(worker, workflowId));
    }

    [Fact]
    public async Task Inactive_workflow_or_unknown_node_is_skipped_without_execution()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker);
        var (workflowId, _) = await worker.SaveWorkflowAsync(OrdersPoll(server, [1]));

        Assert.Equal(PollOutcome.Skipped, (await FireAsync(worker, workflowId)).Outcome);

        await ActivateAsync(worker, workflowId);
        var unknown = await worker.Poll.PollAsync(new PollJobPayload { WorkflowId = workflowId, NodeId = "n_mark" }, CancellationToken.None);
        Assert.Equal(PollOutcome.Skipped, unknown.Outcome);
        Assert.Equal(PollOutcome.Skipped, (await worker.Poll.PollAsync(new PollJobPayload { WorkflowId = Guid.NewGuid(), NodeId = "n_poll" }, CancellationToken.None)).Outcome);

        Assert.Equal(0, await ExecutionCountAsync(worker, workflowId));
        await using var db = worker.Db.CreateDbContext();
        Assert.False(await db.NodeStates.AnyAsync());
    }

    [Fact]
    public async Task Node_error_completes_job_without_execution_and_keeps_state()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker);
        var definition = OrdersPoll(server, [1]);
        definition.Nodes[0].Parameters["toolName"] = "fail";
        definition.Nodes[0].Parameters["arguments"] = new JsonObject { ["reason"] = "джерело недоступне" };
        var (workflowId, _) = await worker.SaveWorkflowAsync(definition);
        await ActivateAsync(worker, workflowId);
        await EnqueuePollAsync(worker, workflowId);

        Assert.Equal(PollOutcome.Failed, (await worker.PollNextAsync())?.Outcome);

        Assert.Equal(0, await worker.JobCountAsync());
        Assert.Equal(0, await ExecutionCountAsync(worker, workflowId));
        await using var db = worker.Db.CreateDbContext();
        Assert.False(await db.NodeStates.AnyAsync());
    }

    [Fact]
    public async Task Concurrent_state_change_discards_records_instead_of_launching_twice()
    {
        await using var worker = await WorkerHarness.CreateAsync(postgres);
        var server = await AddServerAsync(worker);
        var (workflowId, versionId) = await worker.SaveWorkflowAsync(OrdersPoll(server, [1]));
        await ActivateAsync(worker, workflowId);
        Assert.Equal(PollOutcome.Launched, (await FireAsync(worker, workflowId)).Outcome);
        await SetIdsAsync(worker, versionId, server, [1, 2]);

        // Інше опитування тримає рядок node_state: це опитування прочитало стан [1], а зберегти його не встигає.
        await using var other = new NpgsqlConnection(worker.ConnectionString);
        await other.OpenAsync();
        await using var tx = await other.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM node_state WHERE workflow_id = @w FOR UPDATE", other, tx))
        {
            lockRow.Parameters.AddWithValue("w", workflowId);
            await lockRow.ExecuteNonQueryAsync();
        }

        var poll = worker.Poll.PollAsync(new PollJobPayload { WorkflowId = workflowId, NodeId = "n_poll" }, CancellationToken.None);
        await WaitForLockWaitAsync(worker);

        // Конкурентне опитування вже віддало запис 2 і зберегло свій стан.
        await using (var write = new NpgsqlCommand("""UPDATE node_state SET state = '{"seenKeys":[1,2]}'::jsonb WHERE workflow_id = @w""", other, tx))
        {
            write.Parameters.AddWithValue("w", workflowId);
            await write.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        var result = await poll;

        Assert.Equal(PollOutcome.Conflict, result.Outcome);
        Assert.Equal(1, await ExecutionCountAsync(worker, workflowId));
        Assert.Equal(new[] { 1, 2 }, await SeenKeysAsync(worker, workflowId));
    }

    // ---------- допоміжне ----------

    /// <summary><c>mcp.poll</c> по tool'у <c>orders</c> (записи у <c>structuredContent.result</c>) → <c>core.set</c>.</summary>
    private static WorkflowDefinition OrdersPoll(Guid serverId, int[] ids) => WorkflowJson.Deserialize($$"""
        {
          "formatVersion": 1,
          "name": "Нові замовлення",
          "settings": { "timezone": "UTC", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_poll", "name": "Замовлення", "type": "mcp.poll", "typeVersion": 1, "position": [0, 0],
              "parameters": { "serverId": "{{serverId}}", "toolName": "orders", "arguments": { "ids": [{{string.Join(",", ids)}}] },
                              "itemsPath": "result", "keyField": "id", "cron": "* * * * *" } },
            { "id": "n_mark", "name": "Позначка", "type": "core.set", "typeVersion": 1, "position": [200, 0],
              "parameters": { "fields": [ { "name": "seen", "value": "так" } ] } }
          ],
          "connections": [ { "from": { "node": "n_poll" }, "to": { "node": "n_mark" } } ]
        }
        """);

    private static async Task<Guid> AddServerAsync(WorkerHarness worker)
    {
        var server = Workflows.FakeServer("Замовлення (мок)");
        await using var db = worker.Db.CreateDbContext();
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server.Id;
    }

    private static async Task ActivateAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        await db.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(s => s.SetProperty(w => w.Active, true));
    }

    /// <summary>Джерело «змінилось»: поточна версія опитує інші id.</summary>
    private static async Task SetIdsAsync(WorkerHarness worker, Guid versionId, Guid serverId, int[] ids)
    {
        await using var db = worker.Db.CreateDbContext();
        var definition = WorkflowJson.Serialize(OrdersPoll(serverId, ids));
        await db.WorkflowVersions.Where(v => v.Id == versionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.Definition, definition));
    }

    /// <summary>Як <c>ScheduleFireService</c>: job <c>poll</c> у черзі.</summary>
    private static Task EnqueuePollAsync(WorkerHarness worker, Guid workflowId) =>
        worker.Queue.EnqueueAsync(JobKinds.Poll, JobJson.Serialize(new PollJobPayload { WorkflowId = workflowId, NodeId = "n_poll" }));

    /// <summary>Спрацювання розкладу: job у черзі → воркер його бере і обробляє.</summary>
    private static async Task<PollResult> FireAsync(WorkerHarness worker, Guid workflowId)
    {
        await EnqueuePollAsync(worker, workflowId);
        var result = await worker.PollNextAsync();
        Assert.NotNull(result);
        Assert.Equal(0, await PollJobCountAsync(worker));
        return result;
    }

    private static async Task<int[]> StartIdsAsync(WorkerHarness worker, Guid executionId)
    {
        await using var db = worker.Db.CreateDbContext();
        var payload = (await db.Jobs.Where(j => j.Kind == JobKinds.Execution).Select(j => j.Payload).ToListAsync())
            .Select(JobJson.Deserialize<ExecutionJobPayload>)
            .Single(p => p.ExecutionId == executionId);
        Assert.Equal(ExecutionMode.Trigger, payload.Mode);
        Assert.Equal("n_poll", payload.StartNodeId);
        return [.. payload.StartItems!.Select(i => i.Json["id"]!.GetValue<int>())];
    }

    private static async Task<int[]> SeenKeysAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        var state = await db.NodeStates.AsNoTracking().SingleAsync(s => s.WorkflowId == workflowId && s.NodeId == "n_poll");
        return [.. JsonNode.Parse(state.State)!["seenKeys"]!.AsArray().Select(k => k!.GetValue<int>())];
    }

    private static async Task<List<Guid>> ExecutionIdsAsync(WorkerHarness worker, Guid workflowId)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Executions.Where(e => e.WorkflowId == workflowId).Select(e => e.Id).ToListAsync();
    }

    private static async Task<int> ExecutionCountAsync(WorkerHarness worker, Guid workflowId) =>
        (await ExecutionIdsAsync(worker, workflowId)).Count;

    private static async Task<int> PollJobCountAsync(WorkerHarness worker)
    {
        await using var db = worker.Db.CreateDbContext();
        return await db.Jobs.CountAsync(j => j.Kind == JobKinds.Poll);
    }

    private static async Task<Guid> WaitForExecutionAsync(WorkerHarness worker, Guid workflowId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            if ((await ExecutionIdsAsync(worker, workflowId)).FirstOrDefault() is var id && id != Guid.Empty)
            {
                return id;
            }

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50);
        }
    }

    /// <summary>Чекає, поки якийсь запит у БД воркера не стане в очікування блокування рядка.</summary>
    private static async Task WaitForLockWaitAsync(WorkerHarness worker)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var connection = new NpgsqlConnection(worker.ConnectionString);
        await connection.OpenAsync();
        while (true)
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", connection);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50);
        }
    }
}
