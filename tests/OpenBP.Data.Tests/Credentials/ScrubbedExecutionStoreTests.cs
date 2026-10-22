using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenBP.Data.Credentials;
using OpenBP.Engine;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Data.Tests.Credentials;

/// <summary>
/// Інтеграція: воркер виконує сценарій з <see cref="PostgresCredentialProvider"/> і <see cref="PostgresExecutionStore"/>
/// зі спільним <see cref="SecretScrubber"/> — у <c>execution_steps</c> і <c>executions.error</c> секретів немає.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ScrubbedExecutionStoreTests(PostgresFixture postgres)
{
    private const string Secret = "sk-live-integration-secret";
    private const string Password = "pa55-w0rd";
    private const string Leak = "test.leak";
    private const string Echo = "test.echo";
    private const string Fail = "test.fail";

    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Execution_WritesScrubbedStepsAndError()
    {
        var cs = await postgres.MigratedDatabaseAsync();
        var contexts = new PooledDbContextFactory<OpenBpDbContext>(PostgresFixture.Options(cs));
        var credentials = new CredentialStore(contexts, CredentialEncryptionKey.FromBase64(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        var credentialId = await credentials.CreateAsync(
            "api",
            "test",
            new JsonObject { ["apiKey"] = Secret, ["auth"] = new JsonObject { ["password"] = Password } },
            null,
            CancellationToken.None);

        var workflow = Workflow(credentialId);
        var (workflowId, versionId) = await PostgresExecutionStoreTests.SaveVersionAsync(cs, workflow);

        // Одне виконання — один scrubber, спільний для провайдера і сховища (як у воркері).
        var scrubber = new SecretScrubber();
        var store = new PostgresExecutionStore(contexts, scrubber: scrubber);
        var provider = new PostgresCredentialProvider(credentials, scrubber);
        var executionId = Guid.NewGuid();
        await store.CreateAsync(
            new ExecutionSnapshot
            {
                Id = executionId,
                WorkflowId = workflowId.ToString(),
                VersionId = versionId.ToString(),
                Workflow = workflow,
                StartedAt = Now,
            },
            CancellationToken.None);

        var result = await Executor(store, provider).RunAsync(new ExecutionRequest { ExecutionId = executionId, Workflow = workflow }, CancellationToken.None);
        await store.FinishAsync(result, CancellationToken.None);

        // Движок у пам'яті бачив секрети (вони йшли між вузлами), а в БД — лише ***.
        Assert.Equal(ExecutionStatus.Error, result.Status);
        Assert.Contains(Secret, result.Error!.Message, StringComparison.Ordinal);
        Assert.Contains(result.Steps, s => s.Output.SelectMany(p => p).Any(i => i.Json.ToJsonString().Contains(Secret, StringComparison.Ordinal)));

        await using var db = PostgresFixture.CreateContext(cs);
        var rows = await db.ExecutionSteps.AsNoTracking().Where(s => s.ExecutionId == executionId).ToListAsync();
        Assert.Equal(4, rows.Count);
        foreach (var row in rows)
        {
            foreach (var text in new[] { row.Input, row.Output, row.Error })
            {
                Assert.DoesNotContain(Secret, text ?? "", StringComparison.Ordinal);
                Assert.DoesNotContain(Password, text ?? "", StringComparison.Ordinal);
            }
        }

        var leaked = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM execution_steps WHERE execution_id = {executionId} AND (input::text LIKE {"%" + Secret + "%"} OR output::text LIKE {"%" + Secret + "%"} OR error LIKE {"%" + Secret + "%"})")
            .SingleAsync();
        Assert.Equal(0, leaked);

        var execution = await db.Executions.AsNoTracking().SingleAsync(e => e.Id == executionId);
        Assert.Equal(ExecutionStatuses.Error, execution.Status);
        Assert.Equal("Відмова для ***", execution.Error);

        // Знімок для resume читає вже очищені кроки.
        var snapshot = (await store.LoadAsync(executionId, CancellationToken.None))!;
        var leak = snapshot.Steps.Single(s => s.NodeId == "l").Output[0][0].Json;
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""
                    {
                      "header": "Bearer ***",
                      "nested": { "deep": { "password": "***" } },
                      "list": [ "***", "x", [ "Basic ***:***" ] ],
                      "safe": "звичайний текст"
                    }
                    """),
                leak),
            leak.ToJsonString());
        var echo = snapshot.Steps.Single(s => s.NodeId == "e");
        Assert.Equal("***", echo.Input[0][0].Json["list"]![0]!.GetValue<string>());
        var failed = snapshot.Steps.Single(s => s.NodeId == "f");
        Assert.Equal("Відмова для ***", failed.Error!.Message);
    }

    /// <summary>t → l (читає credential і віддає його в items) → e (echo) → f (падає з секретом у повідомленні).</summary>
    private static WorkflowDefinition Workflow(Guid credentialId)
    {
        var wf = new WorkflowDefinition { Name = "Secrets" };
        wf.Nodes.Add(new NodeDefinition { Id = "t", Name = "Тригер", Type = "test.trigger" });
        wf.Nodes.Add(new NodeDefinition { Id = "l", Name = "Витік", Type = Leak, Parameters = new JsonObject { ["credentialId"] = credentialId.ToString() } });
        wf.Nodes.Add(new NodeDefinition { Id = "e", Name = "Відлуння", Type = Echo });
        wf.Nodes.Add(new NodeDefinition { Id = "f", Name = "Падіння", Type = Fail });
        foreach (var (from, to) in new[] { ("t", "l"), ("l", "e"), ("e", "f") })
        {
            wf.Connections.Add(new Connection
            {
                From = new ConnectionEndpoint { Node = from },
                To = new ConnectionEndpoint { Node = to },
            });
        }

        return wf;
    }

    private static WorkflowExecutor Executor(IExecutionStore store, ICredentialProvider credentials)
    {
        var registry = new InMemoryNodeRegistry()
            .Register(new FuncNode(Descriptor("test.trigger", [], ["main"]), _ => Task.FromResult<Item[][]>([[new Item()]])))
            .Register(new FuncNode(
                Descriptor(Leak, ["main"], ["main"], [new CredentialRequirement("api", "test")]),
                async ctx =>
                {
                    var data = await ctx.GetCredentialAsync("api");
                    var key = data["apiKey"]!.GetValue<string>();
                    var password = data["auth"]!["password"]!.GetValue<string>();
                    return [[new Item
                    {
                        Json = new JsonObject
                        {
                            ["header"] = "Bearer " + key,
                            ["nested"] = new JsonObject { ["deep"] = new JsonObject { ["password"] = password } },
                            ["list"] = new JsonArray(key, "x", new JsonArray($"Basic {key}:{password}")),
                            ["safe"] = "звичайний текст",
                        },
                    }]];
                }))
            .Register(new FuncNode(Descriptor(Echo, ["main"], ["main"]), ctx => Task.FromResult<Item[][]>([[.. ctx.Items()]])))
            .Register(new FuncNode(
                Descriptor(Fail, ["main"], ["main"]),
                ctx => throw new InvalidOperationException("Відмова для " + ctx.Items()[0].Json["list"]![0]!.GetValue<string>())));
        return new WorkflowExecutor(registry, store, new FakeExpressionEvaluator(), credentials, new InMemoryBinaryStore(), new InMemoryExecutionLogger());
    }

    private static NodeDescriptor Descriptor(
        string type,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> outputs,
        IReadOnlyList<CredentialRequirement>? credentials = null) =>
        new(type, 1, type, NodeGroup.Logic, inputs, outputs, new JsonObject(), credentials);

    private sealed class FuncNode(NodeDescriptor descriptor, Func<INodeContext, Task<Item[][]>> run) : INode
    {
        public NodeDescriptor Descriptor { get; } = descriptor;

        public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) => run(ctx);
    }
}
