using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Executions;
using OpenBP.Application.Forms;
using OpenBP.Data;
using OpenBP.Engine;
using OpenBP.Shared.Workflow;

namespace OpenBP.Worker.Tests;

/// <summary>
/// F6-03: <c>core.wait</c> v3 у режимі <c>form</c> — <c>$execution.resumeUrl</c> веде на сторінку <c>/form/resume/{token}</c>,
/// опис форми (з обчисленими виразами) береться зі стану кроку <c>waiting</c>, надіслана форма з файлом продовжує
/// виконання: поля і файл (сховище <c>postgres</c>) стають item'ом виходу <c>main</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FormWaitTests(PostgresFixture postgres)
{
    private const string PublicUrl = "https://openbp.test";

    /// <summary>Старт → «Заявка» (id = 7, адреса форми) → форма погодження → «Готово».</summary>
    private static WorkflowDefinition Approval() => WorkflowJson.Deserialize("""
        {
          "formatVersion": 1,
          "name": "Погодження заявки формою",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 60, "errorWorkflowId": null, "saveSuccessfulData": true },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_req", "name": "Заявка", "type": "core.set", "typeVersion": 1, "position": [200, 0],
              "parameters": { "fields": [ { "name": "id", "value": 7 }, { "name": "link", "value": "{{ $execution.resumeUrl }}" } ] } },
            { "id": "n_wait", "name": "Погодження", "type": "core.wait", "typeVersion": 3, "position": [400, 0],
              "parameters": { "mode": "form", "maxWaitSec": 86400, "form": {
                "title": "Погодьте заявку {{ $json.id }}", "submitLabel": "Погодити", "message": "Дякуємо!",
                "schema": { "type": "object", "required": ["decision"], "properties": {
                  "decision": { "type": "string", "enum": ["approve", "reject"] },
                  "scan": { "type": "string", "format": "binary" } } } } } },
            { "id": "n_done", "name": "Готово", "type": "core.set", "typeVersion": 1, "position": [600, 0],
              "parameters": { "fields": [ { "name": "result", "value": "{{ $json.fields.decision }}" } ] } }
          ],
          "connections": [
            { "from": { "node": "n_start" }, "to": { "node": "n_req" } },
            { "from": { "node": "n_req" }, "to": { "node": "n_wait" } },
            { "from": { "node": "n_wait", "output": 0 }, "to": { "node": "n_done" } }
          ]
        }
        """);

    [Fact]
    public async Task Submitted_form_with_file_continues_waiting_execution()
    {
        var worker = await WorkerHarness.CreateAsync(postgres);
        worker.Options.PublicUrl = PublicUrl;
        try
        {
            var (workflowId, versionId) = await worker.SaveWorkflowAsync(Approval());
            var executionId = await worker.LaunchAsync(workflowId, versionId);
            Assert.True(await worker.ProcessNextAsync());
            Assert.Equal(ExecutionStatuses.Waiting, (await worker.ExecutionAsync(executionId)).Status);

            string token;
            await using (var db = worker.Db.CreateDbContext())
            {
                var row = await db.ResumeTokens.AsNoTracking().SingleAsync(t => t.ExecutionId == executionId);
                Assert.Equal(ResumeTokens.FormKind, row.Kind);
                token = row.Token;
            }

            var steps = await worker.StepsAsync(executionId);
            var request = JsonNode.Parse(steps[1].Output!)!.AsArray()[0]!.AsArray()[0]!["json"]!;
            Assert.Equal($"{PublicUrl}/form/resume/{token}", request["link"]!.GetValue<string>());

            await using (var db = worker.Db.CreateDbContext())
            {
                var forms = Forms(worker, db);
                var form = await forms.GetAsync($"resume/{token}", null, CancellationToken.None);
                Assert.Equal(("wait", "Погодьте заявку 7", "Погодити"), (form.Kind, form.Title, form.SubmitLabel));

                var invalid = await Assert.ThrowsAsync<FormValidationException>(() =>
                    forms.SubmitAsync($"resume/{token}", new FormSubmission(new JsonObject { ["decision"] = "maybe" }, []), CancellationToken.None));
                Assert.Equal("decision", Assert.Single(invalid.Errors).Field);

                var submitted = await forms.SubmitAsync(
                    $"resume/{token}",
                    new FormSubmission(new JsonObject { ["decision"] = "approve" }, [new FormUpload("scan", "акт.pdf", "application/pdf", new MemoryStream([5, 6, 7]))]),
                    CancellationToken.None);
                Assert.Equal((executionId, "Дякуємо!"), (submitted.ExecutionId, submitted.Message));

                await Assert.ThrowsAsync<ConflictException>(() => forms.GetAsync($"resume/{token}", null, CancellationToken.None));
            }

            Assert.True(await worker.ProcessNextAsync());
            var done = await worker.ExecutionAsync(executionId);
            Assert.True(done.Status == ExecutionStatuses.Success, done.Error);
            steps = await worker.StepsAsync(executionId);
            var output = JsonNode.Parse(Assert.Single(steps, s => s.NodeId == "n_done").Output!)!.AsArray()[0]!.AsArray();
            var item = Assert.Single(output)!.AsObject();
            Assert.Equal("approve", item["json"]!["result"]!.GetValue<string>());
            Assert.Equal("акт.pdf", item["json"]!["fields"]!["scan"]!.GetValue<string>());
            var scan = item["binary"]!["scan"]!;
            Assert.Equal(PostgresBinaryStore.StorageName, scan["storage"]!.GetValue<string>());

            var store = new PostgresBinaryStore(worker.Db);
            await using var content = await store.OpenReadAsync(new BinaryRef { Id = scan["id"]!.GetValue<string>(), Storage = PostgresBinaryStore.StorageName }, CancellationToken.None);
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes);
            Assert.Equal([5, 6, 7], bytes.ToArray());
            await using (var db = worker.Db.CreateDbContext())
            {
                Assert.Equal(executionId, (await db.BinaryFiles.AsNoTracking().SingleAsync(f => f.Id == scan["id"]!.GetValue<string>())).ExecutionId);
            }
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    private static FormService Forms(WorkerHarness worker, OpenBpDbContext db) => new(
        db,
        new NoLauncher(),
        new WaitResumeService(db, TimeProvider.System),
        new PostgresBinaryStore(worker.Db),
        new Anonymous(),
        () => throw new InvalidOperationException("Форма очікування не читає credentials."),
        FormLimits.Default);

    private sealed class NoLauncher : IExecutionLauncher
    {
        public Task<ExecutionSummary> LaunchAsync(ExecutionLaunch launch, CancellationToken ct) =>
            throw new InvalidOperationException("Форма очікування не запускає нових виконань.");
    }

    private sealed class Anonymous : ICurrentUser
    {
        public bool IsAuthenticated => false;

        public Guid? UserId => null;

        public string? Email => null;

        public UserRole? Role => null;
    }
}
