using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Ai;
using OpenBP.Application.Assistant;
using OpenBP.Application.Auth;
using OpenBP.Application.Jobs;

namespace OpenBP.Api.Tests;

/// <summary>
/// F3-72: пояснення сценарію простими словами — збереження ставить job <c>explain</c> з паузою, генератор (у продукті —
/// воркер) пише пояснення останньої версії, пересування вузлів його не оновлює, помилка моделі видна в стані,
/// без налаштованого помічника — <c>not_configured</c> без job'а; <c>GET</c> читає viewer, <c>POST</c> — editor.
/// </summary>
[Collection(ApiCollection.Name)]
public class ExplanationTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Settings = "/api/v1/settings/assistant";
    private const string ProviderKey = "sk-explain-secret-key";
    private const string PinSecret = "pin-data-real-customer";
    private const string Reply = "Коротко: процес стартує вручну і заповнює поля.";

    private static string Definition(string name, int x = 240) => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_set", "name": "Поля заявки", "type": "core.set", "typeVersion": 1, "position": [{{x}}, 0], "parameters": { "fields": [] } }
          ],
          "connections": [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } }],
          "pinData": { "n_start": [{ "json": { "client": "{{PinSecret}}" } }] }
        }
        """;

    /// <summary>Фейковий OpenAI-сумісний провайдер.</summary>
    private sealed class FakeProvider(Func<int, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Body, string? Authorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue((body, request.Headers.Authorization?.ToString()));
            return reply(Requests.Count);
        }
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text } }),
        }),
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

    private static async Task SaveAsync(HttpClient editor, Guid id, string definition) =>
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsync($"/api/v1/workflows/{id}", Json(definition))).StatusCode);

    private static async Task<JsonElement> ExplanationAsync(HttpClient client, Guid id) =>
        JsonDocument.Parse(await client.GetStringAsync($"/api/v1/workflows/{id}/explanation")).RootElement;

    private async Task<List<ExplainJobPayload>> JobsAsync(Guid id)
    {
        await using var db = api.CreateDbContext();
        var payloads = await db.Jobs.AsNoTracking().Where(j => j.Kind == JobKinds.Explain).Select(j => j.Payload).ToListAsync();
        return [.. payloads.Select(JobJson.Deserialize<ExplainJobPayload>).Where(p => p.WorkflowId == id)];
    }

    [Fact]
    public async Task Save_schedules_explanation_of_the_latest_version_and_moving_nodes_keeps_it()
    {
        var provider = new FakeProvider(_ => Text(Reply));
        await using var factory = WithProvider(provider);
        await ConfigureAsync(await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        var editor = await LoginAsync(factory, UserRole.Editor);
        var name = $"Пояснення {Guid.NewGuid():N}";
        var id = await CreateWorkflowAsync(editor, Definition(name));
        var before = DateTimeOffset.UtcNow;
        await SaveAsync(editor, id, Definition(name, 480));

        // Збереження не чекає моделі: пояснення в черзі з паузою, для кожної версії — свій job.
        var pending = await ExplanationAsync(editor, id);
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.Equal(2, pending.GetProperty("currentVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("text").ValueKind);
        Assert.Equal([1, 2], (await JobsAsync(id)).Select(p => p.Version).Order());
        await using (var db = api.CreateDbContext())
        {
            var runAt = await db.Jobs.AsNoTracking().Where(j => j.Kind == JobKinds.Explain).Select(j => j.RunAt).MaxAsync();
            Assert.True(runAt >= before.AddSeconds(10), "Пояснення має чекати паузу після збереження.");
        }

        using var scope = factory.Services.CreateScope();
        var explainer = scope.ServiceProvider.GetRequiredService<WorkflowExplainer>();
        Assert.Equal(ExplainOutcome.Skipped, await explainer.ExplainAsync(id, 1, CancellationToken.None));
        Assert.Empty(provider.Requests);
        Assert.Equal(ExplainOutcome.Ready, await explainer.ExplainAsync(id, 2, CancellationToken.None));

        var ready = await ExplanationAsync(editor, id);
        Assert.Equal("ready", ready.GetProperty("status").GetString());
        Assert.Equal(Reply, ready.GetProperty("text").GetString());
        Assert.Equal(2, ready.GetProperty("version").GetInt32());
        Assert.False(ready.GetProperty("stale").GetBoolean());

        // Модель бачить назви кроків і з'єднання, але не тестові дані, координати і ключ.
        var (body, authorization) = Assert.Single(provider.Requests);
        Assert.Equal($"Bearer {ProviderKey}", authorization);
        Assert.DoesNotContain(ProviderKey, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PinSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("position", body, StringComparison.Ordinal);
        Assert.Contains("Поля заявки", JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), StringComparison.Ordinal);

        // Пересунули вузол — нова версія, але пояснення те саме, без запиту до моделі.
        await SaveAsync(editor, id, Definition(name, 720));
        Assert.Equal(ExplainOutcome.Unchanged, await explainer.ExplainAsync(id, 3, CancellationToken.None));
        Assert.Single(provider.Requests);
        var moved = await ExplanationAsync(editor, id);
        Assert.Equal("ready", moved.GetProperty("status").GetString());
        Assert.Equal(3, moved.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Provider_error_is_visible_and_request_regenerates()
    {
        var provider = new FakeProvider(i => i == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":{\"message\":\"boom\"}}") }
            : Text(Reply));
        await using var factory = WithProvider(provider);
        await ConfigureAsync(await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword));
        var editor = await LoginAsync(factory, UserRole.Editor);
        var id = await CreateWorkflowAsync(editor, Definition($"Збій {Guid.NewGuid():N}"));
        using var scope = factory.Services.CreateScope();
        var explainer = scope.ServiceProvider.GetRequiredService<WorkflowExplainer>();

        Assert.Equal(ExplainOutcome.Failed, await explainer.ExplainAsync(id, 1, CancellationToken.None));
        var failed = await ExplanationAsync(editor, id);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failed.GetProperty("error").GetString()));

        var requested = await editor.PostAsync($"/api/v1/workflows/{id}/explanation", null);
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        Assert.Equal("pending", JsonDocument.Parse(await requested.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        Assert.Equal(ExplainOutcome.Ready, await explainer.ExplainAsync(id, 1, CancellationToken.None));
        Assert.Equal(Reply, (await ExplanationAsync(editor, id)).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Not_configured_skips_the_job_and_roles_apply()
    {
        var admin = await LoginAsync(api, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Settings, new { credentialId = (Guid?)null, model = (string?)null })).StatusCode);
        var editor = await LoginAsync(api, UserRole.Editor);
        var viewer = await LoginAsync(api, UserRole.Viewer);
        var id = await CreateWorkflowAsync(editor, Definition($"Без моделі {Guid.NewGuid():N}"));

        Assert.Equal("not_configured", (await ExplanationAsync(viewer, id)).GetProperty("status").GetString());
        Assert.Empty(await JobsAsync(id));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/v1/workflows/{id}/explanation", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync($"/api/v1/workflows/{Guid.NewGuid()}/explanation")).StatusCode);

        await ConfigureAsync(admin);
        var requested = await editor.PostAsync($"/api/v1/workflows/{id}/explanation", null);
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        Assert.Equal("pending", JsonDocument.Parse(await requested.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
        Assert.Equal([1], (await JobsAsync(id)).Select(p => p.Version));
    }
}
