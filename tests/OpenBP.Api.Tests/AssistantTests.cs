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
/// F3-60: помічник у редакторі — <c>/settings/assistant</c> і <c>POST /workflows/{id}/assistant</c> проти фейкового
/// OpenAI-сумісного провайдера: структуровані tool-calls змінюють сценарій, ліміт кроків, секрети не йдуть у промпт.
/// </summary>
[Collection(ApiCollection.Name)]
public class AssistantTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Settings = "/api/v1/settings/assistant";
    private const string ProviderKey = "sk-assistant-secret-key";
    private const string NodeSecret = "anthropic-node-secret";

    private static string Definition(string name, string extraNodes = "") => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }{{extraNodes}}],
          "connections": [],
          "pinData": {}
        }
        """;

    /// <summary>Фейковий провайдер: відповідає за сценарієм і запам'ятовує тіла й заголовки запитів.</summary>
    private sealed class FakeProvider(Func<int, JsonObject> reply) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Body, string? Authorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue((body, request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reply(Requests.Count)) };
        }
    }

    private static JsonObject ToolCall(string name, JsonObject arguments, int index) => new()
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = $"call_{index}",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() },
                }),
            },
        }),
    };

    private static JsonObject Text(string text) => new()
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } }),
    };

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string email, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, UserRole role) =>
        await LoginAsync(factory, (await api.CreateUserAsync(role, Pass)).Email, Pass);

    private WebApplicationFactory<Program> WithProvider(FakeProvider provider) =>
        api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new LlmChat(new HttpClient(provider)))));

    private async Task ConfigureAsync(HttpClient admin)
    {
        var credential = await api.CreateCredentialAsync("openai-compatible", new JsonObject { ["baseUrl"] = "http://llm.test/v1", ["apiKey"] = ProviderKey });
        var response = await admin.PutAsJsonAsync(Settings, new { credentialId = credential, model = "fake-model" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Guid> CreateWorkflowAsync(HttpClient editor, string definition)
    {
        var response = await editor.PostAsync("/api/v1/workflows", Json(definition));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<List<(string Type, JsonElement Data)>> AskAsync(HttpClient editor, Guid workflowId, string message)
    {
        var response = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/assistant", new { message });
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

    [Fact]
    public async Task Settings_keep_several_models_and_the_primary_one_is_used()
    {
        var admin = await LoginAsync(api, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var local = await api.CreateCredentialAsync("ollama", new JsonObject());
        var cloud = await api.CreateCredentialAsync("gemini", new JsonObject { ["apiKey"] = ProviderKey });

        var response = await admin.PutAsJsonAsync(Settings, new
        {
            profiles = new[] { new { credentialId = local, model = "qwen3:8b" }, new { credentialId = cloud, model = "gemini-flash" } },
            primaryIndex = 1,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = JsonDocument.Parse(await admin.GetStringAsync(Settings)).RootElement;
        Assert.True(settings.GetProperty("configured").GetBoolean());
        Assert.Equal("gemini", settings.GetProperty("provider").GetString());
        Assert.Equal("gemini-flash", settings.GetProperty("model").GetString());
        Assert.Equal(1, settings.GetProperty("primaryIndex").GetInt32());
        var profiles = settings.GetProperty("profiles").EnumerateArray().ToList();
        Assert.Equal(["ollama", "gemini"], profiles.Select(p => p.GetProperty("provider").GetString()));
        Assert.DoesNotContain(ProviderKey, settings.GetRawText(), StringComparison.Ordinal);

        // Основна має бути зі списку; кожна модель — з credential'ом потрібного типу й назвою моделі.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Settings, new { profiles = new[] { new { credentialId = local, model = "m" } }, primaryIndex = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Settings, new { profiles = new[] { new { credentialId = local, model = " " } } })).StatusCode);

        // Старе тіло {credentialId, model} лишається однією моделлю.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Settings, new { credentialId = local, model = "qwen3:14b" })).StatusCode);
        var single = JsonDocument.Parse(await admin.GetStringAsync(Settings)).RootElement;
        Assert.Equal("qwen3:14b", single.GetProperty("model").GetString());
        Assert.Single(single.GetProperty("profiles").EnumerateArray());
    }

    [Fact]
    public async Task Settings_roles_validation_and_not_configured()
    {
        var factory = api;
        var admin = await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Settings, new { credentialId = (Guid?)null, model = (string?)null })).StatusCode);
        var editor = await LoginAsync(factory, UserRole.Editor);
        var viewer = await LoginAsync(factory, UserRole.Viewer);

        // Не налаштовано — 409 ще до SSE.
        var workflowId = await CreateWorkflowAsync(editor, Definition($"Помічник {Guid.NewGuid():N}"));
        var notConfigured = await editor.PostAsJsonAsync($"/api/v1/workflows/{workflowId}/assistant", new { message = "привіт" });
        Assert.Equal(HttpStatusCode.Conflict, notConfigured.StatusCode);
        Assert.Contains("ASSISTANT_NOT_CONFIGURED", await notConfigured.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Settings)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync(Settings, new { credentialId = Guid.NewGuid(), model = "m" })).StatusCode);

        var wrongType = await api.CreateCredentialAsync("generic", new JsonObject { ["token"] = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Settings, new { credentialId = wrongType, model = "m" })).StatusCode);

        await ConfigureAsync(admin);
        var settings = JsonDocument.Parse(await editor.GetStringAsync(Settings)).RootElement;
        Assert.True(settings.GetProperty("configured").GetBoolean());
        Assert.Equal("openai-compatible", settings.GetProperty("provider").GetString());
        Assert.Equal("fake-model", settings.GetProperty("model").GetString());
        Assert.DoesNotContain(ProviderKey, settings.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_calls_change_the_definition_without_saving_and_secrets_stay_out_of_the_prompt()
    {
        var nodeCredential = await api.CreateCredentialAsync("anthropic", new JsonObject { ["apiKey"] = NodeSecret });
        var withSet = new JsonObject
        {
            ["definition"] = JsonNode.Parse(Definition("будь-яка назва", """
                , { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [240, 0], "parameters": { "fields": [] } }
                """))!.AsObject(),
        };
        withSet["definition"]!["connections"] = JsonNode.Parse("""[{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } }]""");
        var provider = new FakeProvider(i => i switch
        {
            1 => ToolCall("get_workflow", [], i),
            2 => ToolCall("describe_nodes", [], i),
            3 => ToolCall("update_workflow", withSet, i),
            _ => Text("Додав крок «Поля» після тригера."),
        });
        await using var factory = WithProvider(provider);
        await ConfigureAsync(await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        var editor = await LoginAsync(factory, UserRole.Editor);
        var name = $"Помічник {Guid.NewGuid():N}";
        var llmNode = $$""", { "id": "n_llm", "name": "LLM", "type": "ai.llm", "typeVersion": 1, "position": [0, 240], "parameters": { "provider": "anthropic", "model": "m", "prompt": "x", "credentialId": "{{nodeCredential}}" } }""";
        var workflowId = await CreateWorkflowAsync(editor, Definition(name, llmNode));

        var events = await AskAsync(editor, workflowId, "зроби сценарій з тригером і Set");

        Assert.Equal(["step", "step", "step", "definition", "message", "done"], events.Select(e => e.Type));
        Assert.Equal(["get_workflow", "describe_nodes", "update_workflow"], events.Where(e => e.Type == "step").Select(e => e.Data.GetProperty("tool").GetString()));
        Assert.All(events.Where(e => e.Type == "step"), e => Assert.True(e.Data.GetProperty("ok").GetBoolean()));
        var definition = events.Single(e => e.Type == "definition").Data.GetProperty("definition");
        Assert.Equal(["n_start", "n_set"], definition.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()));
        Assert.Equal(name, definition.GetProperty("name").GetString());
        Assert.Equal("Додав крок «Поля» після тригера.", events.Single(e => e.Type == "message").Data.GetProperty("text").GetString());

        // Помічник не зберігає: поточна версія та сама.
        var saved = JsonDocument.Parse(await editor.GetStringAsync($"/api/v1/workflows/{workflowId}")).RootElement;
        Assert.Equal(1, saved.GetProperty("version").GetInt32());

        // Ключ провайдера — лише в Authorization; значення credentials вузлів — ніде.
        Assert.Equal(4, provider.Requests.Count);
        Assert.All(provider.Requests, r =>
        {
            Assert.Equal($"Bearer {ProviderKey}", r.Authorization);
            Assert.DoesNotContain(ProviderKey, r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(NodeSecret, r.Body, StringComparison.Ordinal);
        });
        var first = JsonNode.Parse(provider.Requests.First().Body)!;
        Assert.Equal("fake-model", first["model"]!.GetValue<string>());
        Assert.Equal(
            ["describe_nodes", "describe_node", "describe_connectors", "get_workflow", "update_workflow", "test_node"],
            first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        Assert.Contains(nodeCredential.ToString(), provider.Requests.ElementAt(1).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Describe_nodes_is_a_short_catalogue_and_describe_node_gives_one_schema()
    {
        var provider = new FakeProvider(i => i switch
        {
            1 => ToolCall("describe_nodes", [], i),
            2 => ToolCall("describe_node", new JsonObject { ["type"] = "core.if" }, i),
            3 => ToolCall("describe_node", new JsonObject { ["type"] = "no.such" }, i),
            _ => Text("Готово."),
        });
        await using var factory = WithProvider(provider);
        await ConfigureAsync(await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        var editor = await LoginAsync(factory, UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor, Definition($"Помічник {Guid.NewGuid():N}"));

        var events = await AskAsync(editor, workflowId, "які є вузли");

        Assert.Equal([true, true, false], events.Where(e => e.Type == "step").Select(e => e.Data.GetProperty("ok").GetBoolean()));
        var bodies = provider.Requests.Select(r => r.Body).ToList();
        Assert.Contains("core.manualTrigger", bodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("parametersSchema", bodies[1], StringComparison.Ordinal);
        Assert.Contains("parametersSchema", bodies[2], StringComparison.Ordinal);
        Assert.Contains("NOT_FOUND", bodies[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_after_twenty_tool_steps()
    {
        var provider = new FakeProvider(i => ToolCall("get_workflow", [], i));
        await using var factory = WithProvider(provider);
        await ConfigureAsync(await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        var editor = await LoginAsync(factory, UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor, Definition($"Помічник {Guid.NewGuid():N}"));

        var events = await AskAsync(editor, workflowId, "крутись");

        Assert.Equal(20, events.Count(e => e.Type == "step"));
        var error = events.Single(e => e.Type == "error").Data;
        Assert.Equal("ASSISTANT_STEP_LIMIT", error.GetProperty("code").GetString());
        Assert.Equal("done", events[^1].Type);
        Assert.Equal(21, provider.Requests.Count);
    }

    [Fact]
    public async Task Provider_error_is_an_error_event()
    {
        var provider = new FakeProvider(_ => new JsonObject { ["unexpected"] = true });
        await using var factory = WithProvider(provider);
        await ConfigureAsync(await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        var editor = await LoginAsync(factory, UserRole.Editor);
        var workflowId = await CreateWorkflowAsync(editor, Definition($"Помічник {Guid.NewGuid():N}"));

        var events = await AskAsync(editor, workflowId, "привіт");

        Assert.Equal(["error", "done"], events.Select(e => e.Type));
        Assert.Equal("ASSISTANT_PROVIDER_ERROR", events[0].Data.GetProperty("code").GetString());
    }

    /// <summary>Фейкова Ollama: <c>/api/tags</c> віддає список моделей або помилку.</summary>
    private sealed class FakeOllama(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Enqueue(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Models_ollama_lists_installed_models_and_other_types_are_empty()
    {
        var ollama = new FakeOllama(HttpStatusCode.OK, """{"models":[{"name":"qwen3:8b"},{"name":"qwen3:14b"}]}""");
        var factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new LlmChat(new HttpClient(ollama)))));
        var admin = await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var editor = await LoginAsync(factory, UserRole.Editor);
        var local = await api.CreateCredentialAsync("ollama", new JsonObject { ["baseUrl"] = "http://mac.test:11434" });
        var cloud = await api.CreateCredentialAsync("openai-compatible", new JsonObject { ["baseUrl"] = "http://llm.test/v1" });

        var models = await admin.GetFromJsonAsync<JsonArray>($"{Settings}/models?credentialId={local}");
        Assert.Equal(["qwen3:14b", "qwen3:8b"], models!.Select(m => m!["name"]!.GetValue<string>()));
        Assert.Equal("http://mac.test:11434/api/tags", Assert.Single(ollama.Uris));

        Assert.Empty((await admin.GetFromJsonAsync<JsonArray>($"{Settings}/models?credentialId={cloud}"))!);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Settings, new { credentialId = local, model = "qwen3:14b" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync($"{Settings}/models?credentialId={local}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Settings}/models?credentialId={Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Models_ollama_unreachable_is_a_readable_502()
    {
        var ollama = new FakeOllama(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        var factory = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new LlmChat(new HttpClient(ollama)))));
        var admin = await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var local = await api.CreateCredentialAsync("ollama", new JsonObject());

        var response = await admin.GetAsync($"{Settings}/models?credentialId={local}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("ASSISTANT_PROVIDER_ERROR", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
