using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using OpenBP.Application.Assistant;
using OpenBP.Application.Auth;

namespace OpenBP.Mcp.Server.Tests;

/// <summary>F3-113: помічнику дістаються лише інструменти, доступні ролі; решта не видно й не викликається.</summary>
[Collection(McpServerCollection.Name)]
public class AssistantToolboxTests(McpServerFactory mcp)
{
    private sealed class FakeUser(UserRole role) : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid? UserId { get; } = Guid.NewGuid();

        public string? Email => "assistant@openbp.test";

        public UserRole? Role => role;
    }

    private async Task<IAssistantToolSession> OpenAsync(IServiceScope scope, UserRole role) =>
        await new McpAssistantToolbox(
            scope.ServiceProvider.GetRequiredService<IOptions<McpServerOptions>>(),
            scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>(),
            new FakeUser(role)).OpenAsync(CancellationToken.None);

    [Fact]
    public async Task Tools_follow_the_role_and_hidden_tools_are_not_called()
    {
        using var scope = mcp.Services.CreateScope();
        await using var viewer = await OpenAsync(scope, UserRole.Viewer);
        await using var editor = await OpenAsync(scope, UserRole.Editor);

        Assert.All(viewer.Tools, t => Assert.True(t.ReadOnly, t.Name));
        Assert.Contains(viewer.Tools, t => t.Name == "list_tables");
        Assert.DoesNotContain(viewer.Tools, t => t.Name == "create_table");
        Assert.Contains(editor.Tools, t => t.Name == "create_table");
        Assert.Contains(editor.Tools, t => t.Name == "update_workflow");

        // Пропозиція, збережена раніше, не обходить фільтр: прихований інструмент повертає помилку, не виконуючись.
        var refused = await viewer.CallAsync("create_table", new JsonObject { ["name"] = "x" }, CancellationToken.None);
        Assert.True(refused.IsError);
        Assert.Equal("unknown_tool", (string?)refused.Content?["error"]);

        // Позначка «через помічника» не витікає в потік, що відкрив інструменти.
        Assert.False(AssistantCall.Active);
    }
}
