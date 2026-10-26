using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Mcp.Client;

namespace OpenBP.Api.Tests;

/// <summary>F6-09: змінні інстансу (<c>/variables</c>, попередження валідатора) і експорт/імпорт з перемапінгом.</summary>
[Collection(ApiCollection.Name)]
public class WorkflowTransferTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Variables = "/api/v1/variables";
    private const string Workflows = "/api/v1/workflows";

    // ---------- змінні ----------

    [Fact]
    public async Task Variables_admin_writes_everyone_reads_and_keys_are_checked()
    {
        var admin = await LoginAsync(api, UserRole.Admin);
        var viewer = await LoginAsync(api, UserRole.Viewer);
        var editor = await LoginAsync(api, UserRole.Editor);
        var key = $"region_{Guid.NewGuid():N}";

        var put = await admin.PutAsJsonAsync($"{Variables}/{key}", new { value = "ua-central", description = "  Регіон  " });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Регіон", (await JsonAsync(put)).GetProperty("description").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Variables}/{key}", new { value = "ua-west" })).StatusCode);

        var listed = (await JsonAsync(await viewer.GetAsync(Variables))).EnumerateArray().Single(v => v.GetProperty("key").GetString() == key);
        Assert.Equal("ua-west", listed.GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, listed.GetProperty("description").ValueKind);

        await AssertProblemAsync(await editor.PutAsJsonAsync($"{Variables}/{key}", new { value = "x" }), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await viewer.DeleteAsync($"{Variables}/{key}"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(await admin.PutAsJsonAsync($"{Variables}/1bad", new { value = "x" }), HttpStatusCode.BadRequest, "BAD_REQUEST");
        await AssertProblemAsync(
            await admin.PutAsJsonAsync($"{Variables}/{key}", new { value = new string('x', 10_001) }), HttpStatusCode.BadRequest, "BAD_REQUEST");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Variables}/{key}")).StatusCode);
        await AssertProblemAsync(await admin.DeleteAsync($"{Variables}/{key}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Validate_warns_about_unknown_variable_but_stays_valid()
    {
        var admin = await LoginAsync(api, UserRole.Admin);
        var known = $"known_{Guid.NewGuid():N}";
        var unknown = $"unknown_{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Variables}/{known}", new { value = "1" })).StatusCode);
        var nodes = $$$"""
            { "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
            { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0],
              "parameters": { "fields": [{ "name": "a", "value": "{{ $vars.{{{known}}} }}" }, { "name": "b", "value": "{{ $vars['{{{unknown}}}'] }}" }] } },
            { "id": "n_code", "name": "Код", "type": "core.code", "typeVersion": 1, "position": [400, 0],
              "parameters": { "code": "return { c: $vars.{{{unknown}}} };" } }
            """;
        var connections = """
            [{ "from": { "node": "n_start", "output": 0 }, "to": { "node": "n_set", "input": 0 } },
             { "from": { "node": "n_set", "output": 0 }, "to": { "node": "n_code", "input": 0 } }]
            """;

        var response = await admin.PostAsync($"{Workflows}/validate",
            Json($$"""{ "definition": {{Definition("Змінні", nodes, connections)}} }"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await JsonAsync(response);
        Assert.True(result.GetProperty("isValid").GetBoolean());
        var warnings = result.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.Equal("VARIABLE_UNKNOWN", w.GetProperty("code").GetString()));
        Assert.Equal(["n_set", "n_code"], warnings.Select(w => w.GetProperty("nodeId").GetString()));
        Assert.Equal("/nodes/1/parameters/fields/1/value", warnings[0].GetProperty("path").GetString());
        Assert.Equal("/nodes/2/parameters/code", warnings[1].GetProperty("path").GetString());
    }

    // ---------- експорт / імпорт ----------

    [Fact]
    public async Task Export_then_import_into_second_database_remaps_ids_by_name_and_runs()
    {
        var connectorName = $"1С {Guid.NewGuid():N}";
        var sourceServer = await AddConnectorAsync(api, connectorName);
        var sourceCredential = await api.CreateCredentialAsync("webhook.header", new JsonObject { ["token"] = "src-token" });
        var path = $"hook-{Guid.NewGuid():N}";
        var source = await LoginAsync(api, UserRole.Editor);
        var created = await source.PostAsync(Workflows, Json(Definition("Перенесення", Nodes(path, sourceCredential, sourceServer), Connections)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sourceId = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var exportResponse = await source.GetAsync($"{Workflows}/{sourceId}/export");

        Assert.Equal(HttpStatusCode.OK, exportResponse.StatusCode);
        var export = JsonNode.Parse(await exportResponse.Content.ReadAsStringAsync())!.AsObject();
        var manifest = export["manifest"]!;
        var connector = Assert.Single(manifest["connectors"]!.AsArray())!;
        Assert.Equal(sourceServer.ToString(), connector["ref"]!.GetValue<string>());
        Assert.Equal(connectorName, connector["name"]!.GetValue<string>());
        Assert.Equal("stdio", connector["transport"]!.GetValue<string>());
        var credential = Assert.Single(manifest["credentials"]!.AsArray())!;
        Assert.Equal(sourceCredential.ToString(), credential["ref"]!.GetValue<string>());
        Assert.Equal("webhook.header", credential["type"]!.GetValue<string>());
        Assert.Equal(["region"], manifest["variables"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.DoesNotContain("src-token", export.ToJsonString(), StringComparison.Ordinal);

        var target = new ApiFactory();
        await target.InitializeAsync();
        try
        {
            var targetServer = await AddConnectorAsync(target, connectorName);
            Assert.NotEqual(sourceServer, targetServer);
            var editor = await LoginAsync(target, UserRole.Editor);

            // У приймачі ще немає credential'а з такою назвою і типом — нічого не створюється.
            var unmapped = await editor.PostAsync($"{Workflows}/import", Json(export.ToJsonString()));
            var problem = await AssertProblemAsync(unmapped, HttpStatusCode.UnprocessableEntity, "IMPORT_UNMAPPED");
            var missing = Assert.Single(problem.GetProperty("unmapped").EnumerateArray());
            Assert.Equal("credential", missing.GetProperty("kind").GetString());
            Assert.Equal(sourceCredential.ToString(), missing.GetProperty("ref").GetString());
            Assert.Equal("not_found", missing.GetProperty("reason").GetString());
            Assert.Equal(0, (await JsonAsync(await editor.GetAsync(Workflows))).GetProperty("total").GetInt32());

            var targetCredential = await target.CreateCredentialAsync("webhook.header", new JsonObject { ["token"] = "dst-token" });
            var imported = await editor.PostAsync($"{Workflows}/import", Json(export.ToJsonString()));

            Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
            var result = await JsonAsync(imported);
            Assert.Equal(["region"], result.GetProperty("missingVariables").EnumerateArray().Select(v => v.GetString()));
            var workflow = result.GetProperty("workflow");
            Assert.False(workflow.GetProperty("active").GetBoolean());
            var nodes = workflow.GetProperty("definition").GetProperty("nodes");
            Assert.Equal(targetCredential.ToString(), nodes[0].GetProperty("parameters").GetProperty("auth").GetProperty("credentialId").GetString());
            Assert.Equal(targetServer.ToString(), nodes[1].GetProperty("parameters").GetProperty("serverId").GetString());

            var id = workflow.GetProperty("id").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await editor.PostAsync($"{Workflows}/{id}/activate", null)).StatusCode);
            var client = target.CreateClient();
            using var hook = new HttpRequestMessage(HttpMethod.Post, $"/webhook/{path}") { Content = Json("{}") };
            hook.Headers.Add("X-Hook-Token", "dst-token");
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(hook)).StatusCode);
        }
        finally
        {
            await target.DisposeAsync();
        }
    }

    /// <summary>F3-67: бізнес-розмітка проходить create → get → export → import без втрат.</summary>
    [Fact]
    public async Task Business_fields_survive_create_get_export_and_import()
    {
        var editor = await LoginAsync(api, UserRole.Editor);
        var business = JsonNode.Parse("""
            {
              "lanes": [ { "id": "kit", "name": "Кіт 2", "description": "люди у веб-формах" }, { "id": "openbp", "name": "OpenBP" } ],
              "steps": [ { "id": "s_create", "title": "Автор створює задачу", "lane": "kit",
                           "links": [ { "node": "n_hook", "direction": "in", "label": "бачить за 1–5 хв" } ] } ]
            }
            """)!;
        var definition = JsonNode.Parse(Definition($"Бізнес {Guid.NewGuid():N}", BusinessNodes, BusinessConnections))!.AsObject();
        definition["business"] = business;

        var created = await editor.PostAsync(Workflows, Json(definition.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var read = JsonNode.Parse(await editor.GetStringAsync($"{Workflows}/{id}"))!["definition"]!;
        Assert.True(JsonNode.DeepEquals(definition["business"], read["business"]), read["business"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(definition["nodes"]![0]!["business"], read["nodes"]![0]!["business"]));
        Assert.True(JsonNode.DeepEquals(definition["connections"]![0]!["business"], read["connections"]![0]!["business"]));

        var export = JsonNode.Parse(await editor.GetStringAsync($"{Workflows}/{id}/export"))!.AsObject();
        Assert.True(JsonNode.DeepEquals(business, export["definition"]!["business"]));

        var imported = await editor.PostAsync($"{Workflows}/import", Json(export.ToJsonString()));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var importedDefinition = (await JsonAsync(imported)).GetProperty("workflow").GetProperty("definition");
        var importedNode = JsonNode.Parse(importedDefinition.GetRawText())!;
        Assert.True(JsonNode.DeepEquals(business, importedNode["business"]));
        Assert.Equal("Нова задача", importedNode["nodes"]![0]!["business"]!["title"]!.GetValue<string>());
        Assert.Equal("далі", importedNode["connections"]![0]!["business"]!["label"]!.GetValue<string>());

        // Зламане посилання на доріжку — 422 з кодом і шляхом, як інші помилки валідатора.
        definition["business"]!["steps"]![0]!["lane"] = "nowhere";
        var invalid = await editor.PostAsync(Workflows, Json(definition.ToJsonString()));
        var problem = await AssertProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "WORKFLOW_INVALID");
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal("BUSINESS_LANE_UNKNOWN", error.GetProperty("code").GetString());
        Assert.Equal("/business/steps/0/lane", error.GetProperty("path").GetString());
    }

    private const string BusinessNodes = """
        { "id": "n_hook", "name": "Хук", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
          "parameters": { "method": "POST", "path": "business-hook" }, "business": { "title": "Нова задача", "lane": "openbp" } },
        { "id": "n_set", "name": "Поля", "type": "core.set", "typeVersion": 1, "position": [200, 0],
          "parameters": { "fields": [] }, "business": { "hidden": true } }
        """;

    private const string BusinessConnections =
        """[{ "from": { "node": "n_hook", "output": 0 }, "to": { "node": "n_set", "input": 0 }, "business": { "label": "далі" } }]""";

    [Fact]
    public async Task Import_uses_explicit_mapping_and_rejects_mapping_to_missing_object()
    {
        var name = $"Telegram {Guid.NewGuid():N}";
        var first = await AddConnectorAsync(api, name);
        var second = await AddConnectorAsync(api, name);
        var credential = await api.CreateCredentialAsync("webhook.header", new JsonObject { ["token"] = "t" });
        var editor = await LoginAsync(api, UserRole.Editor);
        var path = $"hook-{Guid.NewGuid():N}";
        var definition = Definition("Явний mapping", Nodes(path, credential, Guid.Parse("00000000-0000-0000-0000-0000000000aa")), Connections);
        var manifest = $$"""
            { "connectors": [{ "ref": "00000000-0000-0000-0000-0000000000aa", "name": "{{name}}", "transport": "stdio" }],
              "credentials": [], "variables": [] }
            """;

        // Дві однакові назви — неоднозначно.
        var ambiguous = await editor.PostAsync($"{Workflows}/import", Json($$"""{ "definition": {{definition}}, "manifest": {{manifest}} }"""));
        var problem = await AssertProblemAsync(ambiguous, HttpStatusCode.UnprocessableEntity, "IMPORT_UNMAPPED");
        Assert.Equal("ambiguous", Assert.Single(problem.GetProperty("unmapped").EnumerateArray()).GetProperty("reason").GetString());

        var invalid = await editor.PostAsync($"{Workflows}/import", Json($$"""
            { "definition": {{definition}}, "manifest": {{manifest}},
              "mapping": { "connectors": { "00000000-0000-0000-0000-0000000000aa": "{{Guid.NewGuid()}}" } } }
            """));
        problem = await AssertProblemAsync(invalid, HttpStatusCode.UnprocessableEntity, "IMPORT_UNMAPPED");
        Assert.Equal("invalid", Assert.Single(problem.GetProperty("unmapped").EnumerateArray()).GetProperty("reason").GetString());

        var mapped = await editor.PostAsync($"{Workflows}/import", Json($$"""
            { "definition": {{definition}}, "manifest": {{manifest}},
              "mapping": { "connectors": { "00000000-0000-0000-0000-0000000000aa": "{{second}}" } } }
            """));
        Assert.Equal(HttpStatusCode.Created, mapped.StatusCode);
        var result = await JsonAsync(mapped);
        Assert.Equal(second.ToString(),
            result.GetProperty("workflow").GetProperty("definition").GetProperty("nodes")[1].GetProperty("parameters").GetProperty("serverId").GetString());
        Assert.NotEqual(first, second);
        // Credential без маніфесту — уже id цього середовища.
        Assert.Equal(credential.ToString(), result.GetProperty("mapping").GetProperty("credentials").GetProperty(credential.ToString()).GetString());
    }

    [Fact]
    public async Task N8n_workflow_converts_and_imports()
    {
        var editor = await LoginAsync(api, UserRole.Editor);
        var n8n = """
            {
              "name": "З n8n",
              "nodes": [
                { "id": "1", "name": "Старт", "type": "n8n-nodes-base.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} },
                { "id": "2", "name": "Код", "type": "n8n-nodes-base.code", "typeVersion": 2, "position": [200, 0],
                  "parameters": { "jsCode": "return [{ json: { ok: true } }];" } },
                { "id": "3", "name": "RSS", "type": "n8n-nodes-base.rssFeedRead", "typeVersion": 1.2, "position": [400, 0], "parameters": { "url": "https://example.com/rss" } }
              ],
              "connections": {
                "Старт": { "main": [[{ "node": "Код", "type": "main", "index": 0 }]] },
                "Код": { "main": [[{ "node": "RSS", "type": "main", "index": 0 }]] }
              }
            }
            """;

        var converted = await editor.PostAsync($"{Workflows}/convert/n8n", Json(n8n));
        Assert.Equal(HttpStatusCode.OK, converted.StatusCode);
        var conversion = await JsonAsync(converted);
        var note = Assert.Single(conversion.GetProperty("notes").EnumerateArray(), n => n.GetProperty("level").GetString() == "replace");
        Assert.Equal("RSS", note.GetProperty("node").GetString());

        var imported = await editor.PostAsync($"{Workflows}/import", Json($$"""
            { "definition": {{conversion.GetProperty("definition").GetRawText()}}, "manifest": {{conversion.GetProperty("manifest").GetRawText()}} }
            """));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var workflow = (await JsonAsync(imported)).GetProperty("workflow");
        Assert.Equal("З n8n", workflow.GetProperty("name").GetString());
        Assert.False(workflow.GetProperty("active").GetBoolean());

        // Telegram — конектор з назвою credential'а n8n і будь-яким транспортом; без збігу — вибір відповідника.
        var bot = $"bot-{Guid.NewGuid():N}";
        var telegram = $$"""
            { "name": "Бот", "connections": {}, "nodes": [{ "id": "1", "name": "Надіслати", "type": "n8n-nodes-base.telegram", "typeVersion": 1.2,
              "position": [0, 0], "parameters": { "chatId": "1", "text": "привіт" }, "credentials": { "telegramApi": { "id": "t1", "name": "{{bot}}" } } }] }
            """;
        conversion = await JsonAsync(await editor.PostAsync($"{Workflows}/convert/n8n", Json(telegram)));
        var unmapped = await editor.PostAsync($"{Workflows}/import", Json($$"""
            { "definition": {{conversion.GetProperty("definition").GetRawText()}}, "manifest": {{conversion.GetProperty("manifest").GetRawText()}} }
            """));
        var problem = await AssertProblemAsync(unmapped, HttpStatusCode.UnprocessableEntity, "IMPORT_UNMAPPED");
        var reference = Assert.Single(problem.GetProperty("unmapped").EnumerateArray());
        Assert.Equal(("connector", bot, JsonValueKind.Null),
            (reference.GetProperty("kind").GetString(), reference.GetProperty("name").GetString(), reference.GetProperty("type").ValueKind));

        await AssertProblemAsync(
            await editor.PostAsync($"{Workflows}/convert/n8n", Json("""{ "nodes": [], "connections": [] }""")), HttpStatusCode.BadRequest, "BAD_REQUEST");
    }

    [Fact]
    public async Task Viewer_cannot_export_or_import()
    {
        var editor = await LoginAsync(api, UserRole.Editor);
        var viewer = await LoginAsync(api, UserRole.Viewer);
        var nodes = """{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }""";
        var definition = Definition("Ролі", nodes, "[]");
        var id = (await JsonAsync(await editor.PostAsync(Workflows, Json(definition)))).GetProperty("id").GetGuid();

        await AssertProblemAsync(await viewer.GetAsync($"{Workflows}/{id}/export"), HttpStatusCode.Forbidden, "FORBIDDEN");
        await AssertProblemAsync(
            await viewer.PostAsync($"{Workflows}/import", Json($$"""{ "definition": {{definition}} }""")), HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // ---------- допоміжне ----------

    private const string Connections = """[{ "from": { "node": "n_hook", "output": 0 }, "to": { "node": "n_tool", "input": 0 } }]""";

    private static string Nodes(string path, Guid credentialId, Guid serverId) => $$$"""
        { "id": "n_hook", "name": "Хук", "type": "core.webhook", "typeVersion": 1, "position": [0, 0],
          "parameters": { "method": "POST", "path": "{{{path}}}", "auth": { "type": "header", "credentialId": "{{{credentialId}}}", "headerName": "X-Hook-Token" } } },
        { "id": "n_tool", "name": "Замовлення", "type": "mcp.tool", "typeVersion": 1, "position": [200, 0],
          "parameters": { "serverId": "{{{serverId}}}", "toolName": "create_order", "arguments": { "customer": "{{ $vars.region }}" } } }
        """;

    private static string Definition(string name, string nodes, string connections) => $$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{{nodes}}],
          "connections": {{connections}},
          "pinData": {}
        }
        """;

    /// <summary>Конектор з tool <c>create_order</c>: рядок <c>mcp_servers</c> (для маніфесту) і реєстр Api (для валідації).</summary>
    private static async Task<Guid> AddConnectorAsync(ApiFactory factory, string name)
    {
        var id = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            db.McpServers.Add(new McpServerEntity { Id = id, Name = name, Transport = "stdio", Config = """{"command":"never-started"}""", Status = "unknown" });
            await db.SaveChangesAsync();
        }

        var registry = factory.Services.GetRequiredService<IMcpServerRegistry>();
        await registry.RegisterAsync(new McpServerConfig { Id = id.ToString(), Name = name, Transport = McpTransportKind.Stdio, Command = "never-started" });
        registry.SetToolsCache(id.ToString(), new ToolsCache(
            [new McpToolInfo("create_order", null, null, JsonNode.Parse("""{ "type": "object", "properties": { "customer": { "type": "string" } } }""")!.AsObject())],
            DateTimeOffset.UtcNow));
        return id;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, UserRole role)
    {
        var user = await factory.CreateUserAsync(role, Pass);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await JsonAsync(response);
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }
}
