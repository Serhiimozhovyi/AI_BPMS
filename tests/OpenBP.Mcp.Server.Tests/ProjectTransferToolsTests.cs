using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenBP.Application.Auth;
using OpenBP.Data;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F8-06: <c>export_project</c> і <c>import_project</c> з тими самими правами, що в REST.</summary>
[Collection(McpServerCollection.Name)]
public class ProjectTransferToolsTests(McpServerFactory mcp)
{
    private static JsonNode Structured(CallToolResult result) => JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;

    [Fact]
    public async Task Editor_exports_and_imports_a_project_and_viewer_cannot_export()
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

        JsonNode bundle;
        await using (var editor = await mcp.ConnectAsync(ownerKey.Key))
        {
            var exported = await editor.CallToolAsync("export_project", new Dictionary<string, object?> { ["projectId"] = project });
            Assert.True(exported.IsError != true, Structured(exported).ToJsonString());
            bundle = Structured(exported);
            Assert.Equal(table, (string)bundle["tables"]![0]!["name"]!);

            var copyName = $"Копія {Guid.NewGuid():N}";
            var imported = await editor.CallToolAsync("import_project", new Dictionary<string, object?> { ["bundle"] = bundle, ["name"] = copyName });
            Assert.True(imported.IsError != true, Structured(imported).ToJsonString());
            Assert.Equal(copyName, (string)Structured(imported)["name"]!);
            Assert.True((bool)Structured(imported)["projectCreated"]!);

            var broken = await editor.CallToolAsync("import_project", new Dictionary<string, object?> { ["bundle"] = new JsonObject { ["formatVersion"] = 99 } });
            Assert.True(broken.IsError);
            Assert.Contains("BUNDLE_VERSION_UNSUPPORTED", Structured(broken).ToJsonString());
        }

        await using var viewer = await mcp.ConnectAsync(readerKey.Key);
        var denied = await viewer.CallToolAsync("export_project", new Dictionary<string, object?> { ["projectId"] = project });
        Assert.True(denied.IsError);
    }
}
