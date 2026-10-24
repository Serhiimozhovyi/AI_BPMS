using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Data;
using OpenBP.Data.Credentials;

namespace OpenBP.Api.Tests;

/// <summary>F4-01: credentials — CRUD, каталог типів, часткова заміна, «використовується», без значень секретів.</summary>
[Collection(ApiCollection.Name)]
public class CredentialTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Base = "/api/v1/credentials";
    private const string Secret = "sk-very-secret-value-42";

    private async Task<HttpClient> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(code, body.GetProperty("code").GetString());
        return body;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, object input)
    {
        var response = await client.PostAsJsonAsync(Base, input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string[] Fields(JsonElement credential) =>
        [.. credential.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!)];

    private static string Unique(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private Task<JsonObject?> DataAsync(Guid id)
    {
        var factory = new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<OpenBpDbContext>(
            new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(api.ConnectionString).Options);
        return new CredentialStore(factory, CredentialEncryptionKey.FromBase64(ApiFactory.EncryptionKey)).GetDataAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task Types_catalog_has_schemas_with_write_only_secrets()
    {
        var admin = await LoginAsync(UserRole.Admin);

        var types = await admin.GetFromJsonAsync<JsonElement>($"{Base}/types");

        var names = types.EnumerateArray().Select(t => t.GetProperty("type").GetString()).ToList();
        Assert.Contains("mcp.http", names);
        Assert.Contains("mcp.stdio", names);
        Assert.Contains("anthropic", names);
        Assert.Contains("generic", names);
        var anthropic = types.EnumerateArray().Single(t => t.GetProperty("type").GetString() == "anthropic");
        var apiKey = anthropic.GetProperty("schema").GetProperty("properties").GetProperty("apiKey");
        Assert.True(apiKey.GetProperty("writeOnly").GetBoolean());
        Assert.Equal("secret", anthropic.GetProperty("fields")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Crud_never_returns_secret_values()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var name = Unique("Anthropic");

        var created = await CreateAsync(admin, new { name, type = "anthropic", data = new { apiKey = Secret } });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(["apiKey"], Fields(created));
        Assert.Equal(name, created.GetProperty("name").GetString());

        var listBody = await (await admin.GetAsync(Base)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, listBody);
        Assert.Contains(id.ToString(), listBody);
        var getBody = await (await admin.GetAsync($"{Base}/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, getBody);

        // У БД — лише шифротекст.
        await using (var db = api.CreateDbContext())
        {
            var row = await db.Credentials.AsNoTracking().SingleAsync(c => c.Id == id);
            Assert.DoesNotContain(Secret, System.Text.Encoding.UTF8.GetString(row.DataEncrypted));
        }

        var renamed = await admin.PatchAsJsonAsync($"{Base}/{id}", new { name = name + " (2)" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(name + " (2)", (await JsonAsync(renamed)).GetProperty("name").GetString());
        Assert.Equal(Secret, (string?)(await DataAsync(id))!["apiKey"]);

        var deleted = await admin.DeleteAsync($"{Base}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await ProblemAsync(await admin.GetAsync($"{Base}/{id}"), HttpStatusCode.NotFound, "NOT_FOUND");
    }

    [Fact]
    public async Task Partial_update_keeps_missing_fields_and_null_removes()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var created = await CreateAsync(admin, new
        {
            name = Unique("Headers"),
            type = "mcp.http",
            data = new { headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Secret, ["X-Tenant"] = "a" } },
        });
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(["headers.Authorization", "headers.X-Tenant"], Fields(created).Order());

        var patch = new JsonObject { ["headers"] = new JsonObject { ["X-Tenant"] = null, ["X-Extra"] = "b" } };
        var response = await admin.PatchAsJsonAsync($"{Base}/{id}", new { data = patch });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["headers.Authorization", "headers.X-Extra"], Fields(await JsonAsync(response)).Order());

        var data = await DataAsync(id);
        Assert.Equal("Bearer " + Secret, (string?)data!["headers"]!["Authorization"]);
        Assert.Equal("b", (string?)data["headers"]!["X-Extra"]);

        // Злиття, що робить дані невалідними (немає обов'язкового поля), — 400.
        var openai = await CreateAsync(admin, new { name = Unique("OpenAI"), type = "openai", data = new { apiKey = Secret } });
        var invalid = await admin.PatchAsJsonAsync($"{Base}/{openai.GetProperty("id").GetGuid()}", new { data = new JsonObject { ["apiKey"] = null } });
        var problem = await ProblemAsync(invalid, HttpStatusCode.BadRequest, "CREDENTIAL_INVALID");
        Assert.Equal("apiKey", problem.GetProperty("errors")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Invalid_type_or_data_is_400_with_errors()
    {
        var admin = await LoginAsync(UserRole.Admin);

        var unknown = await admin.PostAsJsonAsync(Base, new { name = "x", type = "nope", data = new { a = "b" } });
        var p1 = await ProblemAsync(unknown, HttpStatusCode.BadRequest, "CREDENTIAL_INVALID");
        Assert.Equal("type", p1.GetProperty("errors")[0].GetProperty("field").GetString());

        var badUrl = await admin.PostAsJsonAsync(Base, new { name = "x", type = "openai-compatible", data = new { baseUrl = "ftp://x", extra = "y" } });
        var p2 = await ProblemAsync(badUrl, HttpStatusCode.BadRequest, "CREDENTIAL_INVALID");
        var fields = p2.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("field").GetString()).ToList();
        Assert.Contains("baseUrl", fields);
        Assert.Contains("extra", fields);

        var emptyName = await admin.PostAsJsonAsync(Base, new { name = " ", type = "generic", data = new { a = "b" } });
        await ProblemAsync(emptyName, HttpStatusCode.BadRequest, "CREDENTIAL_INVALID");
    }

    [Theory]
    [InlineData(UserRole.Editor)]
    [InlineData(UserRole.Viewer)]
    public async Task Non_admin_is_forbidden(UserRole role)
    {
        var client = await LoginAsync(role);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Base)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/types")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Base, new { name = "x", type = "generic", data = new { a = "b" } })).StatusCode);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Base)).StatusCode);
    }

    [Fact]
    public async Task Used_credential_cannot_be_deleted_and_usage_is_listed()
    {
        var admin = await LoginAsync(UserRole.Admin);
        var byConnector = (await CreateAsync(admin, new { name = Unique("Stdio env"), type = "mcp.stdio", data = new { env = new { TOKEN = Secret } } }))
            .GetProperty("id").GetGuid();
        var byWorkflow = (await CreateAsync(admin, new { name = Unique("Hook"), type = "webhook.header", data = new { token = Secret } }))
            .GetProperty("id").GetGuid();

        var connectorName = Unique("Конектор");
        var workflowName = Unique("Сценарій");
        var connectorId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await using (var db = api.CreateDbContext())
        {
            db.McpServers.Add(new McpServerEntity { Id = connectorId, Name = connectorName, Transport = "stdio", Config = "{}", CredentialId = byConnector, Status = "new" });
            db.Workflows.Add(new WorkflowEntity { Id = workflowId, Name = workflowName, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            db.WorkflowVersions.Add(new WorkflowVersionEntity
            {
                Id = versionId,
                WorkflowId = workflowId,
                Version = 1,
                Definition = $$"""{ "nodes": [ { "id": "n1", "type": "core.webhook", "parameters": { "auth": { "type": "header", "credentialId": "{{byWorkflow}}" } } } ] }""",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            await db.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(u => u.SetProperty(w => w.CurrentVersionId, versionId));
        }

        var c1 = await admin.GetFromJsonAsync<JsonElement>($"{Base}/{byConnector}");
        Assert.Equal(connectorName, c1.GetProperty("usedBy").GetProperty("connectors")[0].GetProperty("name").GetString());
        var c2 = await admin.GetFromJsonAsync<JsonElement>($"{Base}/{byWorkflow}");
        Assert.Equal(workflowName, c2.GetProperty("usedBy").GetProperty("workflows")[0].GetProperty("name").GetString());

        var p1 = await ProblemAsync(await admin.DeleteAsync($"{Base}/{byConnector}"), HttpStatusCode.Conflict, "CREDENTIAL_IN_USE");
        Assert.Contains(connectorName, p1.GetProperty("detail").GetString());
        var p2 = await ProblemAsync(await admin.DeleteAsync($"{Base}/{byWorkflow}"), HttpStatusCode.Conflict, "CREDENTIAL_IN_USE");
        Assert.Contains(workflowName, p2.GetProperty("detail").GetString());

        // Прибрати за собою: інші тести колекції бачать усі конектори.
        await using (var db = api.CreateDbContext())
        {
            await db.McpServers.Where(m => m.Id == connectorId).ExecuteDeleteAsync();
            await db.Workflows.Where(w => w.Id == workflowId).ExecuteUpdateAsync(u => u.SetProperty(w => w.CurrentVersionId, (Guid?)null));
            await db.WorkflowVersions.Where(v => v.WorkflowId == workflowId).ExecuteDeleteAsync();
            await db.Workflows.Where(w => w.Id == workflowId).ExecuteDeleteAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Base}/{byConnector}")).StatusCode);
    }
}
