using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Api.Tests;

/// <summary>
/// F6-04: валідація викликів підсценаріїв (сценарій існує, доступний, має <c>core.workflowTrigger</c>, без циклу
/// A → B → A), заборона активації підсценарію без іншого тригера і зв'язок батьківського й дочірніх виконань у деталях.
/// </summary>
[Collection(ApiCollection.Name)]
public class SubWorkflowTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/workflows";

    private const string WorkflowTrigger = """{ "id": "n_in", "name": "Вхід", "type": "core.workflowTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }""";

    private const string ManualTrigger = """{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }""";

    private static string Call(string workflowId, string id = "n_call") =>
        $$"""{ "id": "{{id}}", "name": "Виклик {{id}}", "type": "core.executeWorkflow", "typeVersion": 1, "position": [200, 0], "parameters": { "workflowId": "{{workflowId}}" } }""";

    private static string Definition(string name, params string[] nodes) => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{{string.Join(", ", nodes)}}],
          "connections": [],
          "pinData": {}
        }
        """;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string definition, Guid? groupId = null)
    {
        var response = await client.PostAsync(groupId is { } g ? $"{Base}?groupId={g}" : Base, Json(definition));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> SingleErrorAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonAsync(response);
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    [Fact]
    public async Task Call_must_target_existing_workflow_with_workflow_trigger()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var plain = await CreateAsync(editor, Definition(Unique("Без тригера підсценарію"), ManualTrigger));
        var child = await CreateAsync(editor, Definition(Unique("Підсценарій"), WorkflowTrigger));

        foreach (var reference in new[] { "не-id", Guid.NewGuid().ToString(), plain.ToString() })
        {
            var error = await SingleErrorAsync(
                await editor.PostAsync(Base, Json(Definition(Unique("Викликає"), ManualTrigger, Call(reference)))), "SUBWORKFLOW_INVALID");
            Assert.Equal("/nodes/1/parameters/workflowId", error.GetProperty("path").GetString());
            Assert.Equal("n_call", error.GetProperty("nodeId").GetString());
        }

        await CreateAsync(editor, Definition(Unique("Викликає"), ManualTrigger, Call(child.ToString())));
        // Вираз перевіряє лише воркер під час виконання.
        await CreateAsync(editor, Definition(Unique("Викликає"), ManualTrigger, Call("{{ $json.flow }}")));
    }

    [Fact]
    public async Task Call_cycle_is_rejected()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var a = await CreateAsync(editor, Definition(Unique("А"), WorkflowTrigger));
        var b = await CreateAsync(editor, Definition(Unique("Б"), WorkflowTrigger, Call(a.ToString())));
        var c = await CreateAsync(editor, Definition(Unique("В"), WorkflowTrigger, Call(b.ToString())));

        // А → В → Б → А — цикл через два сценарії; А → А — сам собі.
        var cycle = await SingleErrorAsync(await editor.PutAsync($"{Base}/{a}", Json(Definition(Unique("А"), WorkflowTrigger, Call(c.ToString())))), "SUBWORKFLOW_CYCLE");
        Assert.Contains(" → ", cycle.GetProperty("message").GetString(), StringComparison.Ordinal);
        await SingleErrorAsync(await editor.PutAsync($"{Base}/{a}", Json(Definition(Unique("А"), WorkflowTrigger, Call(a.ToString())))), "SUBWORKFLOW_CYCLE");

        // Без циклу — можна.
        var d = await CreateAsync(editor, Definition(Unique("Г"), WorkflowTrigger));
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"{Base}/{a}", Json(Definition(Unique("А"), WorkflowTrigger, Call(d.ToString()))))).StatusCode);
    }

    [Fact]
    public async Task Call_to_other_project_is_rejected()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var created = await admin.PostAsJsonAsync("/api/v1/workflow-groups", new { name = Unique("Проєкт") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = (await JsonAsync(created)).GetProperty("id").GetGuid();
        var inProject = await CreateAsync(admin, Definition(Unique("Підсценарій проєкту"), WorkflowTrigger), project);
        var shared = await CreateAsync(admin, Definition(Unique("Спільний підсценарій"), WorkflowTrigger));

        await SingleErrorAsync(await admin.PostAsync(Base, Json(Definition(Unique("Спільний"), ManualTrigger, Call(inProject.ToString())))), "SUBWORKFLOW_INVALID");
        await CreateAsync(admin, Definition(Unique("У проєкті"), ManualTrigger, Call(inProject.ToString())), project);
        await CreateAsync(admin, Definition(Unique("У проєкті"), ManualTrigger, Call(shared.ToString())), project);
    }

    [Fact]
    public async Task Sub_workflow_publishes_but_does_not_activate_alone()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var child = await CreateAsync(editor, Definition(Unique("Підсценарій"), WorkflowTrigger));

        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{child}/publish", null)).StatusCode);
        await SingleErrorAsync(await editor.PostAsync($"{Base}/{child}/activate", null), "WORKFLOW_TRIGGER_STANDALONE");

        // З іншим тригером поруч — активується.
        var both = await CreateAsync(editor, Definition(Unique("Підсценарій із розкладом"), WorkflowTrigger, ManualTrigger.Replace("n_start", "n_manual", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Base}/{both}/activate", null)).StatusCode);
    }

    [Fact]
    public async Task Execution_detail_links_parent_and_children()
    {
        var editor = await LoginAsync(UserRole.Editor);
        var child = await CreateAsync(editor, Definition(Unique("Підсценарій"), WorkflowTrigger));
        var parent = await CreateAsync(editor, Definition(Unique("Викликає"), ManualTrigger, Call(child.ToString())));
        var parentExecution = Guid.CreateVersion7();
        var childExecution = Guid.CreateVersion7();
        await using (var db = api.CreateDbContext())
        {
            var parentVersion = db.Workflows.Single(w => w.Id == parent).CurrentVersionId!.Value;
            var childVersion = db.Workflows.Single(w => w.Id == child).CurrentVersionId!.Value;
            db.Executions.Add(new ExecutionEntity { Id = parentExecution, WorkflowId = parent, VersionId = parentVersion, Mode = "manual", Status = ExecutionStatuses.Success });
            db.Executions.Add(new ExecutionEntity
            {
                Id = childExecution,
                WorkflowId = child,
                VersionId = childVersion,
                Mode = "sub",
                Status = ExecutionStatuses.Success,
                ParentExecutionId = parentExecution,
                ParentNodeId = "n_call",
            });
            await db.SaveChangesAsync();
        }

        var detail = await JsonAsync(await editor.GetAsync($"/api/v1/executions/{parentExecution}"));
        var summary = Assert.Single(detail.GetProperty("children").EnumerateArray());
        Assert.Equal(childExecution, summary.GetProperty("id").GetGuid());
        Assert.Equal("sub", summary.GetProperty("mode").GetString());
        Assert.Equal("n_call", summary.GetProperty("parentNodeId").GetString());

        var childDetail = await JsonAsync(await editor.GetAsync($"/api/v1/executions/{childExecution}"));
        Assert.Equal(parentExecution, childDetail.GetProperty("summary").GetProperty("parentExecutionId").GetGuid());
        Assert.Empty(childDetail.GetProperty("children").EnumerateArray());
    }
}
