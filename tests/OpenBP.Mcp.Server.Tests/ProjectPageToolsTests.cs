using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F8-04: <c>upsert_project_page</c> і <c>list_project_pages</c> з тими самими правами й перевіркою, що в REST.</summary>
[Collection(McpServerCollection.Name)]
public class ProjectPageToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    [Fact]
    public async Task Editor_upserts_a_page_and_viewer_lists_it_but_cannot_change_it()
    {
        var (owner, ownerKey) = await mcp.CreateApiKeyAsync(UserRole.Editor);
        var (reader, readerKey) = await mcp.CreateApiKeyAsync(UserRole.Viewer);
        var project = Guid.NewGuid();
        await using (var db = mcp.CreateDbContext())
        {
            var name = $"Проєкт {project:N}";
            db.WorkflowGroups.Add(new WorkflowGroupEntity { Id = project, Name = name, NormalizedName = name.ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow });
            db.ProjectMembers.Add(new ProjectMemberEntity { ProjectId = project, UserId = owner.Id, Role = "editor" });
            db.ProjectMembers.Add(new ProjectMemberEntity { ProjectId = project, UserId = reader.Id, Role = "viewer" });
            await db.SaveChangesAsync();
        }

        using var http = mcp.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerKey.Key);
        var table = $"events_{Guid.NewGuid():N}";
        var created = await http.PostAsJsonAsync("/api/v1/tables", new { name = table, projectId = project, columns = new[] { new { name = "title", type = "string" } } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var definition = new Dictionary<string, object?> { ["table"] = table, ["columns"] = new[] { new { column = "title", label = "Подія" } } };
        await using (var editor = await mcp.ConnectAsync(ownerKey.Key))
        {
            var saved = await editor.CallToolAsync("upsert_project_page", new Dictionary<string, object?>
            {
                ["projectId"] = project,
                ["key"] = "events",
                ["name"] = "Події",
                ["kind"] = "list",
                ["definition"] = definition,
            });
            Assert.True(saved.IsError != true, Structured(saved).ToJsonString());
            Assert.Equal("events", (string)Structured(saved)["key"]!);

            var bad = await editor.CallToolAsync("upsert_project_page", new Dictionary<string, object?>
            {
                ["projectId"] = project,
                ["key"] = "bad",
                ["name"] = "x",
                ["kind"] = "list",
                ["definition"] = new Dictionary<string, object?> { ["table"] = table, ["columns"] = new[] { new { column = "nope" } } },
            });
            Assert.True(bad.IsError);
            Assert.Contains("PAGE_COLUMN_UNKNOWN", Structured(bad).ToJsonString());
        }

        await using var viewer = await mcp.ConnectAsync(readerKey.Key);
        var listed = Structured(await viewer.CallToolAsync("list_project_pages", new Dictionary<string, object?> { ["projectId"] = project }));
        Assert.Equal(["events"], listed["items"]!.AsArray().Select(p => (string)p!["key"]!).ToList());
        var denied = await viewer.CallToolAsync("upsert_project_page", new Dictionary<string, object?>
        {
            ["projectId"] = project,
            ["key"] = "events",
            ["name"] = "Хак",
            ["kind"] = "list",
            ["definition"] = definition,
        });
        Assert.True(denied.IsError);
    }
}
