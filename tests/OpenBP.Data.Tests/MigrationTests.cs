using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpenBP.Data.Tests;

[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private async Task<string> NewDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(postgres.ConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    private static OpenBpDbContext Create(string cs) =>
        new(new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(cs).Options);

    [Fact]
    public async Task Migration_applies_to_clean_database_and_matches_model()
    {
        await using var db = Create(await NewDatabaseAsync());

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        // Модель не змінилась після міграції `Initial` — нової міграції генерувати не треба.
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Every_table_round_trips_with_jsonb()
    {
        var cs = await NewDatabaseAsync();
        await using (var db = Create(cs))
        {
            await db.Database.MigrateAsync();
        }

        var userId = Guid.NewGuid();
        var wfId = Guid.NewGuid();
        var verId = Guid.NewGuid();
        var execId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await using (var db = Create(cs))
        {
            db.Users.Add(new UserEntity { Id = userId, Email = "a@b.c", PasswordHash = "h", Role = "admin", CreatedAt = Now });
            db.ApiKeys.Add(new ApiKeyEntity { Id = Guid.NewGuid(), UserId = userId, Hash = "k", Name = "ci", LastUsedAt = Now });
            db.Workflows.Add(new WorkflowEntity { Id = wfId, Name = "wf", Active = true, CurrentVersionId = verId, CreatedBy = userId, UpdatedAt = Now });
            db.WorkflowVersions.Add(new WorkflowVersionEntity { Id = verId, WorkflowId = wfId, Version = 1, Definition = """{"nodes":[{"id":"a"}]}""", CreatedBy = userId, CreatedAt = Now });
            db.Credentials.Add(new CredentialEntity { Id = Guid.NewGuid(), Name = "c", Type = "openai", DataEncrypted = [1, 2, 3], Nonce = [4, 5], CreatedBy = userId });
            db.McpServers.Add(new McpServerEntity { Id = Guid.NewGuid(), Name = "s", Transport = "http", Config = """{"url":"http://x"}""", Status = "ok", ToolsCache = """[{"name":"t"}]""", ToolsCachedAt = Now });
            db.Executions.Add(new ExecutionEntity { Id = execId, WorkflowId = wfId, VersionId = verId, Mode = "manual", Status = "running", StartedAt = Now, HeartbeatAt = Now });
            db.ExecutionSteps.Add(new ExecutionStepEntity { Id = Guid.NewGuid(), ExecutionId = execId, NodeId = "a", RunIndex = 0, Status = "ok", Input = """{"i":1}""", Output = """{"o":[1,2]}""", StartedAt = Now, FinishedAt = Now });
            db.Jobs.Add(new JobEntity { Id = jobId, Kind = "execution", Payload = """{"executionId":"x"}""", RunAt = Now, Attempts = 0 });
            db.Webhooks.Add(new WebhookEntity { Id = Guid.NewGuid(), WorkflowId = wfId, NodeId = "a", Method = "POST", Path = "hook/1" });
            db.NodeStates.Add(new NodeStateEntity { WorkflowId = wfId, NodeId = "a", State = """{"cursor":5}""" });
            await db.SaveChangesAsync();
        }

        await using (var db = Create(cs))
        {
            Assert.Equal("admin", (await db.Users.SingleAsync()).Role);
            Assert.Equal("ci", (await db.ApiKeys.SingleAsync()).Name);
            Assert.Equal(verId, (await db.Workflows.SingleAsync()).CurrentVersionId);
            Assert.Contains("\"nodes\"", (await db.WorkflowVersions.SingleAsync()).Definition);
            Assert.Equal(new byte[] { 1, 2, 3 }, (await db.Credentials.SingleAsync()).DataEncrypted);
            Assert.Contains("\"url\"", (await db.McpServers.SingleAsync()).Config);
            Assert.Equal("running", (await db.Executions.SingleAsync()).Status);
            Assert.Contains("\"o\"", (await db.ExecutionSteps.SingleAsync()).Output);
            Assert.Equal("execution", (await db.Jobs.SingleAsync()).Kind);
            Assert.Equal("hook/1", (await db.Webhooks.SingleAsync()).Path);
            Assert.Contains("\"cursor\"", (await db.NodeStates.SingleAsync()).State);
        }

        // jsonb справді: Postgres сам розбирає JSON.
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var q = new NpgsqlCommand("SELECT definition->'nodes'->0->>'id' FROM workflow_versions", conn);
        Assert.Equal("a", (string?)await q.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Webhook_path_must_be_unique()
    {
        var cs = await NewDatabaseAsync();
        await using var db = Create(cs);
        await db.Database.MigrateAsync();
        db.Webhooks.Add(new WebhookEntity { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), NodeId = "a", Path = "p" });
        db.Webhooks.Add(new WebhookEntity { Id = Guid.NewGuid(), WorkflowId = Guid.NewGuid(), NodeId = "b", Path = "p" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Required_indexes_exist()
    {
        var cs = await NewDatabaseAsync();
        await using (var db = Create(cs))
        {
            await db.Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var q = new NpgsqlCommand("SELECT tablename, indexdef FROM pg_indexes WHERE schemaname = 'public'", conn);
        var defs = new List<string>();
        await using (var r = await q.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                defs.Add(r.GetString(0) + ": " + r.GetString(1));
            }
        }

        Assert.Contains(defs, d => d.StartsWith("webhooks:", StringComparison.Ordinal) && d.Contains("UNIQUE", StringComparison.Ordinal) && d.Contains("(path)", StringComparison.Ordinal));
        Assert.Contains(defs, d => d.StartsWith("jobs:", StringComparison.Ordinal) && d.Contains("(run_at)", StringComparison.Ordinal) && d.Contains("WHERE (locked_by IS NULL)", StringComparison.Ordinal));
        Assert.Contains(defs, d => d.StartsWith("execution_steps:", StringComparison.Ordinal) && d.Contains("(execution_id)", StringComparison.Ordinal));
    }
}
