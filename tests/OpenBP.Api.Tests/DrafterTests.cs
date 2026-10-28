using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Ai;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F3-103: <c>POST /workflows/{id}/draft</c> — побудова сценарію з опису проти фейкового OpenAI-сумісного провайдера:
/// план → компіляція → параметри по вузлу, повтори плану й вузла, todo, секрети не йдуть у промпт.
/// </summary>
[Collection(ApiCollection.Name)]
public class DrafterTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string ProviderKey = "sk-drafter-secret-key";
    private const string NodeSecret = "drafter-node-secret";
    private const string Description = "Коли менеджер запускає процес, перевірити суму; якщо більше 1000 — директор погоджує, інакше просто записати.";

    /// <summary>Фейковий провайдер: план чи параметри вузла — за текстом запиту; відповіді задає тест.</summary>
    private sealed class FakeProvider(Func<string, int, string> plan, Func<string, int, string> node) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> attempts = new();

        public ConcurrentQueue<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(body);
            var messages = JsonNode.Parse(body)!["messages"]!.AsArray();
            var first = messages.First(m => (string?)m!["role"] == "user")!["content"]!.GetValue<string>();
            string content;
            if (first.StartsWith("Process description:", StringComparison.Ordinal))
            {
                var name = first.Split("Node: «")[1].Split('»')[0];
                content = node(name, attempts.AddOrUpdate(name, 1, (_, n) => n + 1));
            }
            else
            {
                content = plan(first, attempts.AddOrUpdate("plan", 1, (_, n) => n + 1));
            }

            var reply = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply) };
        }
    }

    private static string Plan(string triggerUse = "core.manualTrigger") => $$"""
        {"title":"Погодження суми","lanes":["Менеджер","Директор"],"steps":[
          {"id":"s1","kind":"trigger","title":"Менеджер запускає","lane":"Менеджер","use":"{{triggerUse}}"},
          {"id":"s2","kind":"condition","title":"Сума більше 1000?","lane":"Менеджер","use":"core.if","hint":"сума"},
          {"id":"s3","kind":"approval","title":"Директор погоджує","lane":"Директор","use":"core.approval"},
          {"id":"s4","kind":"action","title":"Записати","lane":"Менеджер","use":"none"}],
         "links":[{"from":"s1","to":"s2"},{"from":"s2","to":"s3","branch":"true"},{"from":"s2","to":"s4","branch":"false"},
          {"from":"s3","to":"s4","branch":"decision0"}]}
        """;

    private const string IfParams = """{"conditions":[{"left":"{{ $json.amount }}","operator":"gt","right":1000}]}""";
    private const string ApprovalParams = """{"title":"Погодити суму","assignee":{"role":"admin"},"credentialId":"stolen"}""";

    private static string GoodNode(string name) => name switch
    {
        "Сума більше 1000?" => IfParams,
        "Директор погоджує" => ApprovalParams,
        _ => "{}",
    };

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private async Task<(HttpClient Editor, Guid WorkflowId)> PrepareAsync(FakeProvider provider)
    {
        var factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new LlmChat(new HttpClient(provider)))));
        var admin = await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var credential = await api.CreateCredentialAsync("openai-compatible", new JsonObject { ["baseUrl"] = "http://llm.test/v1", ["apiKey"] = ProviderKey });
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/assistant", new { credentialId = credential, model = "fake-model" })).StatusCode);
        var editor = await LoginAsync(factory, (await api.CreateUserAsync(UserRole.Editor, Pass)).Email, Pass);

        var nodeCredential = await api.CreateCredentialAsync("anthropic", new JsonObject { ["apiKey"] = NodeSecret });
        var definition = $$"""
            {
              "formatVersion": 1,
              "name": "Чернетка {{Guid.NewGuid():N}}",
              "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
              "nodes": [{ "id": "n_llm", "name": "LLM", "type": "ai.llm", "typeVersion": 1, "position": [0, 0],
                "parameters": { "provider": "anthropic", "model": "m", "prompt": "{{NodeSecret}}", "credentialId": "{{nodeCredential}}" } }],
              "connections": [],
              "pinData": {}
            }
            """;
        var created = await editor.PostAsync("/api/v1/workflows", new StringContent(definition, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (editor, JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid());
    }

    private static async Task<List<(string Type, JsonElement Data)>> DraftAsync(HttpClient editor, Guid workflowId, string description)
    {
        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/draft", new { description });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        return
        [
            .. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(block =>
            {
                var lines = block.Split('\n');
                var type = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))["event: ".Length..];
                var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
                return (type, JsonDocument.Parse(data).RootElement.Clone());
            }),
        ];
    }

    private static JsonElement Node(JsonElement definition, string id) =>
        definition.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == id);

    [Fact]
    public async Task Description_becomes_a_valid_workflow_with_branches_lanes_and_todo()
    {
        var provider = new FakeProvider((_, _) => Plan(), (name, _) => GoodNode(name));
        var (editor, workflowId) = await PrepareAsync(provider);

        var events = await DraftAsync(editor, workflowId, Description);

        Assert.Equal("done", events[^1].Type);
        Assert.DoesNotContain(events, e => e.Type == "error");
        Assert.Equal(["plan", "plan", "compile", "node", "node", "node", "node"], events.Where(e => e.Type == "step").Select(e => e.Data.GetProperty("stage").GetString()));
        var definition = events.Single(e => e.Type == "definition").Data.GetProperty("definition");

        // Назва — від сценарію, вузли — з плану; старі вузли (і їхні секрети) не лишаються.
        Assert.StartsWith("Чернетка", definition.GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Equal(["n_s1", "n_s2", "n_s3", "n_s4"], definition.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()));
        Assert.Equal("gt", Node(definition, "n_s2").GetProperty("parameters").GetProperty("conditions")[0].GetProperty("operator").GetString());
        var approval = Node(definition, "n_s3").GetProperty("parameters");
        Assert.Equal("admin", approval.GetProperty("assignee").GetProperty("role").GetString());
        Assert.False(approval.TryGetProperty("credentialId", out _));

        // Гілки: if true → погодження, false → запис; погодження decision0 (вихід 1) → запис.
        var connections = definition.GetProperty("connections").EnumerateArray()
            .Select(c => $"{c.GetProperty("from").GetProperty("node").GetString()}:{c.GetProperty("from").GetProperty("output").GetInt32()}>{c.GetProperty("to").GetProperty("node").GetString()}")
            .ToList();
        Assert.Equal(["n_s1:0>n_s2", "n_s2:0>n_s3", "n_s2:1>n_s4", "n_s3:1>n_s4"], connections);

        var lanes = definition.GetProperty("business").GetProperty("lanes").EnumerateArray().ToDictionary(l => l.GetProperty("name").GetString()!, l => l.GetProperty("id").GetString());
        Assert.Equal(["Менеджер", "Директор"], lanes.Keys);
        Assert.Equal(lanes["Директор"], Node(definition, "n_s3").GetProperty("business").GetProperty("lane").GetString());

        // Що доналаштувати: заглушка «Записати».
        var todo = events.Single(e => e.Type == "todo").Data.GetProperty("todo").EnumerateArray().ToList();
        Assert.Contains(todo, t => t.GetProperty("nodeId").GetString() == "n_s4");

        // Сценарій валідний.
        var validation = await editor.PostAsJsonAsync("/api/v1/workflows/validate", new { definition = JsonNode.Parse(definition.GetRawText()) });
        Assert.Contains("\"isValid\":true", (await validation.Content.ReadAsStringAsync()).Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // Ключ провайдера — лише в заголовку; секрети сценарію й облікових даних — ніде в промптах.
        Assert.All(provider.Requests, body =>
        {
            Assert.DoesNotContain(ProviderKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain(NodeSecret, body, StringComparison.Ordinal);
        });

        // План просили зі схемою: use — закритий список з каталогу.
        var planRequest = JsonNode.Parse(provider.Requests.First())!;
        var uses = planRequest["response_format"]!["json_schema"]!["schema"]!["properties"]!["steps"]!["items"]!["properties"]!["use"]!["enum"]!.AsArray();
        Assert.Contains("core.approval", uses.Select(u => (string?)u));
        Assert.Contains("none", uses.Select(u => (string?)u));
    }

    [Fact]
    public async Task Unknown_use_retries_the_plan_and_broken_parameters_retry_the_node()
    {
        var provider = new FakeProvider(
            (_, attempt) => attempt == 1 ? Plan("core.startProcess") : Plan(),
            (name, attempt) => name == "Директор погоджує" && attempt == 1 ? "{ title: зламано" : GoodNode(name));
        var (editor, workflowId) = await PrepareAsync(provider);

        var events = await DraftAsync(editor, workflowId, Description);

        var steps = events.Where(e => e.Type == "step").Select(e => (Stage: e.Data.GetProperty("stage").GetString(), Ok: e.Data.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, Text: e.Data.GetProperty("text").GetString())).ToList();
        Assert.Contains(steps, s => s.Stage == "plan" && !s.Ok && s.Text!.Contains("core.startProcess", StringComparison.Ordinal));
        Assert.Contains(steps, s => s.Stage == "retry" && s.Text!.Contains("Директор погоджує", StringComparison.Ordinal));
        Assert.Equal(2, steps.Count(s => s.Stage == "node" && s.Ok));

        var definition = events.Single(e => e.Type == "definition").Data.GetProperty("definition");
        Assert.Equal("core.manualTrigger", Node(definition, "n_s1").GetProperty("type").GetString());
        Assert.Equal("Погодити суму", Node(definition, "n_s3").GetProperty("parameters").GetProperty("title").GetString());

        // Повтор плану отримав текст помилки.
        var retry = provider.Requests.Select(b => JsonNode.Parse(b)!["messages"]!.AsArray()).First(m => m.Count > 2);
        Assert.Contains("core.startProcess", retry[^1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Node_that_fails_three_times_stays_with_errors_in_todo()
    {
        var provider = new FakeProvider((_, _) => Plan(), (name, _) => name == "Сума більше 1000?" ? """{"conditions":"не масив"}""" : GoodNode(name));
        var (editor, workflowId) = await PrepareAsync(provider);

        var events = await DraftAsync(editor, workflowId, Description);

        Assert.Equal(2, events.Count(e => e.Type == "step" && e.Data.GetProperty("stage").GetString() == "retry"));
        Assert.Equal(4, events.Single(e => e.Type == "definition").Data.GetProperty("definition").GetProperty("nodes").GetArrayLength());
        var todo = events.Single(e => e.Type == "todo").Data.GetProperty("todo").EnumerateArray().ToList();
        Assert.Contains(todo, t => t.GetProperty("nodeId").GetString() == "n_s2");
        Assert.Equal("done", events[^1].Type);
    }

    [Fact]
    public async Task Plan_that_never_fits_is_an_error_and_empty_description_is_400()
    {
        var provider = new FakeProvider((_, _) => """{"title":"x","lanes":[],"steps":[{"id":"s1","kind":"action","title":"Дія","use":"core.set"}],"links":[]}""", (name, _) => GoodNode(name));
        var (editor, workflowId) = await PrepareAsync(provider);

        var events = await DraftAsync(editor, workflowId, Description);

        Assert.Equal(["error", "done"], events.Where(e => e.Type is "error" or "done" or "definition").Select(e => e.Type));
        Assert.Equal("DRAFT_PLAN_FAILED", events.Single(e => e.Type == "error").Data.GetProperty("code").GetString());
        Assert.Equal(3, provider.Requests.Count);

        var empty = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/draft", new { description = " " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }
}
