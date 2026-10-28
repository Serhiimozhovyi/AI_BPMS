using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Ai;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Tests;

/// <summary>
/// F3-108: розмови загального помічника — <c>/assistant/conversations</c> проти фейкового OpenAI-сумісного провайдера:
/// інструменти MCP у процесі, кілька кроків, пропозиції змін з рішенням людини, контекст сторінки, права за роллю.
/// </summary>
[Collection(ApiCollection.Name)]
public class AssistantConversationTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Conversations = "/api/v1/assistant/conversations";

    /// <summary>Фейковий провайдер: відповідає за номером запиту й запам'ятовує тіла.</summary>
    private sealed class FakeProvider(Func<int, JsonObject> reply) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
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

    private async Task ConfigureAsync(WebApplicationFactory<Program> factory)
    {
        var admin = await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var credential = await api.CreateCredentialAsync("openai-compatible", new JsonObject { ["baseUrl"] = "http://llm.test/v1", ["apiKey"] = "sk-test" });
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/settings/assistant", new { credentialId = credential, model = "fake-model" })).StatusCode);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<Guid> CreateConversationAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(Conversations, new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<List<(string Type, JsonElement Data)>> SendAsync(HttpClient client, Guid id, object body)
    {
        var response = await client.PostAsJsonAsync($"{Conversations}/{id}/messages", body);
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

    private static string LastUserText(string requestBody) =>
        JsonNode.Parse(requestBody)!["messages"]!.AsArray().Last(m => (string?)m!["role"] == "user")!["content"]!.ToString();

    [Fact]
    public async Task Reads_run_at_once_changes_wait_for_the_user_and_the_decision_reaches_the_model()
    {
        var tableName = $"Заявки {Guid.NewGuid():N}";
        var createTable = new JsonObject
        {
            ["name"] = tableName,
            ["columns"] = new JsonArray(new JsonObject { ["name"] = "number", ["type"] = "string", ["key"] = true }),
            ["planId"] = "підставлений моделлю",
        };
        var provider = new FakeProvider(i => i switch
        {
            1 => ToolCall("list_tables", [], i),
            2 => ToolCall("create_table", createTable, i),
            3 => Text("Пропоную створити таблицю «Заявки» з колонкою number."),
            _ => Text("Таблицю створено."),
        });
        var factory = WithProvider(provider);
        await ConfigureAsync(factory);
        var editor = await LoginAsync(factory, UserRole.Editor);
        var workflowId = Guid.NewGuid();
        var id = await CreateConversationAsync(editor);

        var events = await SendAsync(editor, id, new
        {
            message = "Зроби таблицю для заявок",
            context = new { page = "/tables", workflowId },
        });
        Assert.Equal(["step", "step", "proposal", "message", "done"], events.Select(e => e.Type));
        Assert.Equal("list_tables", events[0].Data.GetProperty("tool").GetString());
        Assert.True(events[0].Data.GetProperty("ok").GetBoolean());
        var proposal = events[2].Data.GetProperty("proposal");
        Assert.Equal("create_table", proposal.GetProperty("tool").GetString());
        Assert.Equal("pending", proposal.GetProperty("status").GetString());
        Assert.Equal("create", proposal.GetProperty("plan").GetProperty("action").GetString());
        Assert.False(proposal.GetProperty("arguments").TryGetProperty("planId", out _));

        // Інструменти — tools /mcp; контекст сторінки йде моделі разом із запитом.
        var first = JsonNode.Parse(provider.Requests.First())!;
        var toolNames = first["tools"]!.AsArray().Select(t => (string)t!["function"]!["name"]!).ToList();
        Assert.Contains("list_tables", toolNames);
        Assert.Contains("create_table", toolNames);
        Assert.Contains($"[Контекст: сторінка: /tables; сценарій: {workflowId}]", LastUserText(provider.Requests.First()), StringComparison.Ordinal);
        var planned = JsonNode.Parse(provider.Requests.ElementAt(2))!["messages"]!.AsArray().Last()!["content"]!.ToString();
        Assert.Contains("awaiting_confirmation", planned, StringComparison.Ordinal);

        // Пропозиція ще не виконана: таблиці немає.
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync($"/api/v1/tables/{Uri.EscapeDataString(tableName)}")).StatusCode);
        var conversation = JsonDocument.Parse(await editor.GetStringAsync($"{Conversations}/{id}")).RootElement;
        Assert.Equal("Зроби таблицю для заявок", conversation.GetProperty("title").GetString());
        Assert.Equal(["user", "tool", "tool", "assistant"], conversation.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        var proposalId = conversation.GetProperty("proposals")[0].GetProperty("id").GetGuid();

        var applied = await editor.PostAsJsonAsync($"{Conversations}/{id}/proposals/{proposalId}", new { apply = true });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("applied", (await JsonAsync(applied)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"/api/v1/tables/{Uri.EscapeDataString(tableName)}")).StatusCode);
        var again = await editor.PostAsJsonAsync($"{Conversations}/{id}/proposals/{proposalId}", new { apply = true });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("ASSISTANT_PROPOSAL_DECIDED", (await JsonAsync(again)).GetProperty("code").GetString());

        // Наступне повідомлення: модель бачить історію й примітку про рішення.
        events = await SendAsync(editor, id, new { message = "Що далі?" });
        Assert.Equal(["message", "done"], events.Select(e => e.Type));
        var next = provider.Requests.Last();
        Assert.Contains("[Примітка: Користувач застосував пропозицію «create_table»", LastUserText(next), StringComparison.Ordinal);
        var firstUser = JsonNode.Parse(next)!["messages"]!.AsArray().First(m => (string?)m!["role"] == "user")!["content"]!.ToString();
        Assert.Contains("Зроби таблицю для заявок", firstUser, StringComparison.Ordinal);

        var list = JsonDocument.Parse(await editor.GetStringAsync(Conversations)).RootElement;
        Assert.Equal(id, list[0].GetProperty("id").GetGuid());
        Assert.Equal(4, list[0].GetProperty("messageCount").GetInt32());
    }

    [Fact]
    public async Task Rejected_proposal_changes_nothing()
    {
        var group = $"Проєкт {Guid.NewGuid():N}";
        var provider = new FakeProvider(i => i == 1 ? ToolCall("create_workflow_group", new JsonObject { ["name"] = group }, i) : Text("Створю проєкт після підтвердження."));
        var factory = WithProvider(provider);
        await ConfigureAsync(factory);
        var editor = await LoginAsync(factory, UserRole.Editor);
        var id = await CreateConversationAsync(editor);

        var events = await SendAsync(editor, id, new { message = "Створи проєкт" });
        var proposal = events.Single(e => e.Type == "proposal").Data.GetProperty("proposal");
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("plan").ValueKind);

        var rejected = await editor.PostAsJsonAsync($"{Conversations}/{id}/proposals/{proposal.GetProperty("id").GetGuid()}", new { apply = false });
        Assert.Equal("rejected", (await JsonAsync(rejected)).GetProperty("status").GetString());
        var groups = await editor.GetStringAsync("/api/v1/workflow-groups");
        Assert.DoesNotContain(group, groups, StringComparison.Ordinal);
        var conversation = JsonDocument.Parse(await editor.GetStringAsync($"{Conversations}/{id}")).RootElement;
        Assert.Equal("note", conversation.GetProperty("messages").EnumerateArray().Last().GetProperty("role").GetString());
    }

    [Fact]
    public async Task Only_editors_talk_and_a_conversation_is_seen_by_its_author_and_admin()
    {
        var provider = new FakeProvider(_ => Text("Привіт."));
        var factory = WithProvider(provider);
        await ConfigureAsync(factory);
        var author = await LoginAsync(factory, UserRole.Editor);
        var other = await LoginAsync(factory, UserRole.Editor);
        var viewer = await LoginAsync(factory, UserRole.Viewer);
        var admin = await LoginAsync(factory, ApiFactory.AdminEmail, ApiFactory.AdminPassword);
        var id = await CreateConversationAsync(author);
        await SendAsync(author, id, new { message = "Привіт" });

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Conversations)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Conversations, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Conversations}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{Conversations}/{id}/messages", new { message = "чуже" })).StatusCode);
        Assert.DoesNotContain(id.ToString(), await other.GetStringAsync(Conversations), StringComparison.Ordinal);

        var authorId = JsonDocument.Parse(await author.GetStringAsync($"{Conversations}/{id}")).RootElement.GetProperty("userId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"{Conversations}/{id}")).StatusCode);
        Assert.Contains(id.ToString(), await admin.GetStringAsync($"{Conversations}?userId={authorId}"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{Conversations}?userId={authorId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync($"{Conversations}/{id}/messages", new { message = "від admin" })).StatusCode);

        var tooLong = await author.PostAsJsonAsync($"{Conversations}/{id}/messages", new { message = new string('я', 8001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await author.DeleteAsync($"{Conversations}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await author.GetAsync($"{Conversations}/{id}")).StatusCode);
    }
}
