using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Auth;
using OpenBP.Application.Executions;
using OpenBP.Data;

namespace OpenBP.Api.Tests;

/// <summary>F6-15: журнал аудиту — запис кожної дії, без секретів, <c>GET /audit</c> лише для admin, ретеншн.</summary>
[Collection(ApiCollection.Name)]
public class AuditTests(ApiFactory api)
{
    private const string Pass = "User-pass-1";
    private const string Audit = "/api/v1/audit";

    private static string Unique(string name) => $"{name} {Guid.NewGuid():N}";

    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

    private static string FakeServerDll => Path.Combine(AppContext.BaseDirectory, "OpenBP.Mcp.FakeServer.dll");

    private static StringContent Definition(string name) => new($$"""
        {
          "formatVersion": 1,
          "name": "{{name}}",
          "settings": { "timezone": "Europe/Kyiv", "executionTimeoutSec": 0, "errorWorkflowId": null, "saveSuccessfulData": false },
          "nodes": [{ "id": "n_start", "name": "Старт", "type": "core.manualTrigger", "typeVersion": 1, "position": [0, 0], "parameters": {} }],
          "connections": [],
          "pinData": {}
        }
        """, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<(HttpClient Client, UserEntity User)> LoginAsync(UserRole role)
    {
        var user = await api.CreateUserAsync(role, Pass);
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user.Email, password = Pass });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        return (client, user);
    }

    /// <summary>Записи сутності від старих до нових.</summary>
    private static async Task<JsonElement[]> EntriesAsync(HttpClient admin, string entityType, string entityId)
    {
        var page = await JsonAsync(await admin.GetAsync($"{Audit}?entityType={entityType}&entityId={Uri.EscapeDataString(entityId)}&pageSize=200"));
        return [.. page.GetProperty("items").EnumerateArray().Reverse()];
    }

    private static string[] Actions(JsonElement[] entries) => [.. entries.Select(e => e.GetProperty("action").GetString()!)];

    [Fact]
    public async Task Workflow_lifecycle_is_audited_with_user()
    {
        var (admin, user) = await LoginAsync(UserRole.Admin);
        var name = Unique("Аудит");
        var created = await admin.PostAsync("/api/v1/workflows", Definition(name));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/v1/workflows/{id}", Definition(name))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/workflows/{id}", new { name = name + " 2" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/workflows/{id}/activate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/workflows/{id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/workflows/{id}")).StatusCode);

        var entries = await EntriesAsync(admin, "workflow", id);
        Assert.Equal(
            ["workflow.created", "workflow.saved", "workflow.renamed", "workflow.activated", "workflow.deactivated", "workflow.deleted"],
            Actions(entries));
        Assert.All(entries, e =>
        {
            Assert.Equal(user.Id, e.GetProperty("userId").GetGuid());
            Assert.Equal(user.Email, e.GetProperty("userEmail").GetString());
            Assert.Equal(JsonValueKind.Null, e.GetProperty("apiKeyId").ValueKind);
            Assert.Equal("workflow", e.GetProperty("entityType").GetString());
        });
        Assert.Equal(2, entries[1].GetProperty("details").GetProperty("version").GetInt32());
        Assert.Equal(name, entries[2].GetProperty("details").GetProperty("previousName").GetString());
        Assert.Equal(name + " 2", entries[5].GetProperty("details").GetProperty("name").GetString());

        // Фільтр за префіксом дії і користувачем.
        var byPrefix = await JsonAsync(await admin.GetAsync($"{Audit}?action=workflow.&userId={user.Id}"));
        Assert.Equal(6, byPrefix.GetProperty("total").GetInt32());
        var exact = await JsonAsync(await admin.GetAsync($"{Audit}?action=workflow.activated&userId={user.Id}"));
        Assert.Equal(1, exact.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Credential_changes_are_audited_without_secret_values()
    {
        var (admin, _) = await LoginAsync(UserRole.Admin);
        const string secret = "sk-audit-super-secret-1";
        const string replaced = "sk-audit-super-secret-2";
        var created = await admin.PostAsJsonAsync("/api/v1/credentials", new { name = Unique("Ключ"), type = "generic", data = new { token = secret } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/credentials/{id}", new { name = Unique("Ключ 2") })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/v1/credentials/{id}", new { data = new { token = replaced } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/credentials/{id}")).StatusCode);

        var entries = await EntriesAsync(admin, "credential", id);
        Assert.Equal(["credential.created", "credential.renamed", "credential.replaced", "credential.deleted"], Actions(entries));
        Assert.Equal("token", entries[2].GetProperty("details").GetProperty("fields")[0].GetString());
        Assert.Equal("generic", entries[0].GetProperty("details").GetProperty("type").GetString());

        await using var db = api.CreateDbContext();
        var raw = await db.AuditLog.Where(a => a.EntityId == id).Select(a => a.Details).ToListAsync();
        Assert.All(raw, d =>
        {
            Assert.DoesNotContain(secret, d, StringComparison.Ordinal);
            Assert.DoesNotContain(replaced, d, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Connector_api_key_variable_and_table_changes_are_audited()
    {
        var (admin, user) = await LoginAsync(UserRole.Admin);

        var connector = await admin.PostAsJsonAsync("/api/v1/connectors", new
        {
            name = Unique("Фейк"),
            transport = "stdio",
            config = new { command = DotNet, arguments = new[] { FakeServerDll }, environment = new Dictionary<string, string> { ["TOKEN"] = "env-secret-value" } },
        });
        Assert.Equal(HttpStatusCode.Created, connector.StatusCode);
        var connectorId = (await JsonAsync(connector)).GetProperty("summary").GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/connectors/{connectorId}")).StatusCode);
        var connectorEntries = await EntriesAsync(admin, "connector", connectorId);
        Assert.Equal(["connector.created", "connector.deleted"], Actions(connectorEntries));
        Assert.Equal("stdio", connectorEntries[0].GetProperty("details").GetProperty("transport").GetString());
        Assert.DoesNotContain("env-secret-value", connectorEntries[0].GetRawText(), StringComparison.Ordinal);

        // Ключ створено в UI-сесії, а змінну змінено через REST цим ключем — у записі видно ключ.
        var key = await JsonAsync(await admin.PostAsJsonAsync("/api/v1/api-keys", new { name = "аудит" }));
        var keyId = key.GetProperty("id").GetString()!;
        using var byKey = api.CreateClient();
        byKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.GetProperty("key").GetString());
        var variable = $"audit_{Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.OK, (await byKey.PutAsJsonAsync($"/api/v1/variables/{variable}", new { value = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await byKey.PutAsJsonAsync($"/api/v1/variables/{variable}", new { value = "2" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await byKey.DeleteAsync($"/api/v1/variables/{variable}")).StatusCode);
        var variableEntries = await EntriesAsync(admin, "variable", variable);
        Assert.Equal(["variable.set", "variable.set", "variable.deleted"], Actions(variableEntries));
        Assert.True(variableEntries[0].GetProperty("details").GetProperty("created").GetBoolean());
        Assert.False(variableEntries[1].GetProperty("details").GetProperty("created").GetBoolean());
        Assert.All(variableEntries, e =>
        {
            Assert.Equal(keyId, e.GetProperty("apiKeyId").GetString());
            Assert.Equal("аудит", e.GetProperty("apiKeyName").GetString());
            Assert.Equal(user.Id, e.GetProperty("userId").GetGuid());
        });

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/api-keys/{keyId}")).StatusCode);
        var keyEntries = await EntriesAsync(admin, "apiKey", keyId);
        Assert.Equal(["apiKey.created", "apiKey.revoked"], Actions(keyEntries));
        Assert.DoesNotContain(key.GetProperty("key").GetString()!, keyEntries[0].GetRawText(), StringComparison.Ordinal);
        // Відкликаний ключ лишається в записах за id, назви вже немає.
        Assert.Equal(JsonValueKind.Null, (await EntriesAsync(admin, "variable", variable))[0].GetProperty("apiKeyName").ValueKind);

        var tableName = Unique("Таблиця");
        var table = await admin.PostAsJsonAsync("/api/v1/tables", new { name = tableName, columns = new object[] { new { name = "a", type = "string" } } });
        var tableId = (await JsonAsync(table)).GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/tables/{tableId}", new { name = tableName, columns = new object[] { new { name = "b", type = "number" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/tables/{tableId}")).StatusCode);
        Assert.Equal(["dataTable.created", "dataTable.updated", "dataTable.deleted"], Actions(await EntriesAsync(admin, "dataTable", tableId)));
    }

    [Fact]
    public async Task Only_admin_reads_the_audit_log()
    {
        var (viewer, _) = await LoginAsync(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Audit)).StatusCode);
        var (editor, _) = await LoginAsync(UserRole.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(Audit)).StatusCode);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Audit)).StatusCode);
    }

    [Fact]
    public async Task Retention_deletes_entries_older_than_audit_days()
    {
        var now = new DateTimeOffset(2031, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var tag = Guid.NewGuid().ToString("N");
        await using (var db = api.CreateDbContext())
        {
            foreach (var age in new[] { 400, 366, 364, 1 })
            {
                db.AuditLog.Add(new AuditEntryEntity
                {
                    Id = Guid.NewGuid(),
                    At = now.AddDays(-age),
                    Action = "workflow.saved",
                    EntityType = "workflow",
                    EntityId = $"{tag}-{age}",
                    Details = "{}",
                });
            }

            await db.SaveChangesAsync();
        }

        await using (var db = api.CreateDbContext())
        {
            var zero = new ExecutionRetentionService(db, new ExecutionRetentionOptions { AuditDays = 0 }, new FixedTime(now));
            Assert.Equal(0, await zero.PurgeAuditAsync(default));
            var service = new ExecutionRetentionService(db, new ExecutionRetentionOptions { AuditDays = 365, BatchSize = 1 }, new FixedTime(now));
            Assert.True(await service.PurgeAuditAsync(default) >= 2);
        }

        await using var check = api.CreateDbContext();
        var left = await check.AuditLog.Where(a => a.EntityId!.StartsWith(tag)).Select(a => a.EntityId).ToListAsync();
        Assert.Equal([$"{tag}-1", $"{tag}-364"], left.Order());
        Assert.Equal(365, ExecutionRetentionOptions.Parse(null, null).AuditDays);
        Assert.Equal(90, ExecutionRetentionOptions.Parse(null, null, "90").AuditDays);
        Assert.Throws<ArgumentException>(() => ExecutionRetentionOptions.Parse(null, null, "-1"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
