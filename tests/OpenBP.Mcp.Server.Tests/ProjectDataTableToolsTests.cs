using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F8-02: <c>list_tables</c>/<c>query_table</c> з проєктами, чутливі колонки і ресурс <c>openbp://projects/{id}/tables</c>.</summary>
[Collection(McpServerCollection.Name)]
public class ProjectDataTableToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.True(result.IsError != true, $"{tool}: {Structured(result).ToJsonString()}");
        return Structured(result);
    }

    /// <summary>Проєкт, де <paramref name="members"/> — учасники з ролями.</summary>
    private async Task<Guid> CreateProjectAsync(params (Guid UserId, string Role)[] members)
    {
        await using var db = mcp.CreateDbContext();
        var name = $"Проєкт {Guid.NewGuid():N}";
        var project = new WorkflowGroupEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = name.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow };
        db.WorkflowGroups.Add(project);
        foreach (var (userId, role) in members)
        {
            db.ProjectMembers.Add(new ProjectMemberEntity { ProjectId = project.Id, UserId = userId, Role = role });
        }

        await db.SaveChangesAsync();
        return project.Id;
    }

    private HttpClient Http(string apiKey)
    {
        var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    [Fact]
    public async Task Project_tables_are_visible_only_to_members_and_sensitive_values_only_to_editors()
    {
        var (owner, ownerKey) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        var (reader, readerKey) = await mcp.CreateApiKeyAsync(UserRole.Viewer);
        var (_, strangerKey) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        var project = await CreateProjectAsync((owner.Id, "editor"), (reader.Id, "viewer"));

        using var http = Http(ownerKey.Key);
        var name = $"Клієнти {Guid.NewGuid():N}";
        var created = await http.PostAsJsonAsync("/api/v1/tables", new
        {
            name,
            projectId = project,
            columns = new object[] { new { name = "name", type = "string" }, new { name = "phone", type = "string", sensitive = true } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (string)(await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]!;
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsJsonAsync($"/api/v1/tables/{id}/rows", new { values = new { name = "Олена", phone = "+380501234567" } })).StatusCode);

        // Не учасник проєкту таблиці не бачить.
        await using (var stranger = await mcp.ConnectAsync(strangerKey.Key))
        {
            Assert.DoesNotContain((await CallAsync(stranger, "list_tables", new()))["items"]!.AsArray(), t => (string)t!["id"]! == id);
            var hidden = await stranger.CallToolAsync("query_table", new Dictionary<string, object?> { ["table"] = id });
            Assert.True(hidden.IsError);
            await Assert.ThrowsAsync<McpProtocolException>(() => stranger.ReadResourceAsync($"openbp://tables/{id}").AsTask());
        }

        // Viewer проєкту бачить рядки, але не чутливі значення і не може відбирати за ними.
        await using (var viewer = await mcp.ConnectAsync(readerKey.Key))
        {
            var listed = (await CallAsync(viewer, "list_tables", new() { ["projectId"] = project }))["items"]!.AsArray();
            var table = listed.Single(t => (string)t!["id"]! == id)!;
            Assert.Equal(project.ToString(), (string)table["projectId"]!);
            Assert.True((bool)table["columns"]![1]!["sensitive"]!);
            var rows = (await CallAsync(viewer, "query_table", new() { ["table"] = name, ["projectId"] = project }))["rows"]!.AsArray();
            Assert.Equal("Олена", (string)rows[0]!["data"]!["name"]!);
            Assert.Null(rows[0]!["data"]!["phone"]);
            var byPhone = await viewer.CallToolAsync("query_table", new Dictionary<string, object?>
            {
                ["table"] = id,
                ["where"] = new JsonArray(new JsonObject { ["column"] = "phone", ["operator"] = "contains", ["value"] = "501" }),
            });
            Assert.True(byPhone.IsError);
        }

        // Editor бачить чутливі значення в query_table (з записом в аудит), але не в ресурсі.
        await using (var editor = await mcp.ConnectAsync(ownerKey.Key))
        {
            var rows = (await CallAsync(editor, "query_table", new() { ["table"] = id }))["rows"]!.AsArray();
            Assert.Equal("+380501234567", (string)rows[0]!["data"]!["phone"]!);
            var resource = Assert.IsType<TextResourceContents>(Assert.Single((await editor.ReadResourceAsync($"openbp://tables/{id}")).Contents));
            var resourceRow = JsonNode.Parse(resource.Text)!["rows"]![0]!["data"]!;
            Assert.Equal("Олена", (string)resourceRow["name"]!);
            Assert.Null(resourceRow["phone"]);

            var projectTables = Assert.IsType<TextResourceContents>(Assert.Single((await editor.ReadResourceAsync($"openbp://projects/{project}/tables")).Contents));
            Assert.Contains(JsonNode.Parse(projectTables.Text)!["items"]!.AsArray(), t => (string)t!["id"]! == id);
        }

        await using var db = mcp.CreateDbContext();
        Assert.Equal(1, await db.AuditLog.CountAsync(a => a.EntityId == id && a.Action == "dataTable.sensitiveRead"));
    }
}
