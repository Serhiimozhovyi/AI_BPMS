using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBP.Ai;
using OpenBP.Application.Assistant;
using OpenBP.Application.Jobs;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F3-72: воркер бере job <see cref="JobKinds.Explain"/> і пише пояснення сценарію моделлю помічника; некоректний
/// payload завершує job без повторів.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ExplainJobTests(PostgresFixture postgres)
{
    private const string Reply = "Коротко: процес чекає і позначає готовність.";

    private sealed class FakeProvider : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply } }),
                }),
            });
        }
    }

    private static async Task ConfigureAssistantAsync(WorkerHarness worker)
    {
        var credential = await worker.Credentials.CreateAsync(
            "llm", "openai-compatible", new JsonObject { ["baseUrl"] = "http://llm.test/v1", ["apiKey"] = "sk-test" }, createdBy: null, CancellationToken.None);
        await using var db = worker.Db.CreateDbContext();
        db.AppSettings.Add(new AppSettingEntity
        {
            Key = AssistantSettingsService.Key,
            Value = new JsonObject { ["credentialId"] = credential.ToString(), ["model"] = "fake-model" }.ToJsonString(),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static ExplainJobProcessor Processor(WorkerHarness worker, FakeProvider provider) => new(
        worker.Queue,
        new WorkflowExplainer(worker.Db, CredentialEncryptionKey.FromBase64(worker.EncryptionKey), new LlmChat(new HttpClient(provider)), TimeProvider.System),
        worker.Options,
        NullLogger<ExplainJobProcessor>.Instance);

    [Fact]
    public async Task Worker_takes_explain_job_and_stores_explanation()
    {
        var worker = await WorkerHarness.CreateAsync(postgres);
        var provider = new FakeProvider();
        var host = new ExecutionWorker(worker.Queue, worker.Processor, worker.Options, NullLogger<ExecutionWorker>.Instance, poll: worker.Poll, explain: Processor(worker, provider));
        try
        {
            await ConfigureAssistantAsync(worker);
            var (workflowId, _) = await worker.SaveWorkflowAsync(Workflows.Waiting(10));
            await worker.Queue.EnqueueAsync(JobKinds.Explain, JobJson.Serialize(new ExplainJobPayload { WorkflowId = workflowId, Version = 1 }));

            await host.StartAsync(CancellationToken.None);
            WorkflowExplanationEntity? row = null;
            for (var i = 0; i < 100 && row?.Status != WorkflowExplanationStatus.Ready; i++)
            {
                await Task.Delay(100);
                await using var db = worker.Db.CreateDbContext();
                row = await db.WorkflowExplanations.AsNoTracking().SingleOrDefaultAsync(e => e.WorkflowId == workflowId);
            }

            Assert.NotNull(row);
            Assert.Equal(WorkflowExplanationStatus.Ready, row.Status);
            Assert.Equal(Reply, row.Text);
            Assert.Equal(1, row.Version);
            Assert.Equal(1, provider.Calls);
            Assert.Equal(0, await worker.JobCountAsync());
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bad_payload_completes_the_job()
    {
        var worker = await WorkerHarness.CreateAsync(postgres);
        try
        {
            await worker.Queue.EnqueueAsync(JobKinds.Explain, "{\"workflowId\":\"not-a-guid\"}");
            var job = await worker.Queue.ClaimAsync(worker.Options.WorkerId, [JobKinds.Explain]);
            Assert.NotNull(job);

            Assert.Null(await Processor(worker, new FakeProvider()).ProcessAsync(job, CancellationToken.None));
            Assert.Equal(0, await worker.JobCountAsync());
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }
}
