using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Application.Connectors;
using OpenBP.Application.Nodes;
using OpenBP.Application.Paging;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;

namespace OpenBP.Application.Tests;

/// <summary>F3-102: стислий каталог вузлів і дій конекторів для малих моделей.</summary>
public class CatalogDigestTests
{
    private static readonly JsonObject Schema = new() { ["type"] = "object", ["properties"] = new JsonObject { ["big"] = new string('x', 5000) } };

    private static NodeDescriptor Node(string type, int version, NodeGroup group, string[] inputs, string[]? outputs = null, string? description = null) =>
        new(type, version, type + " назва", group, inputs, outputs ?? ["main"], Schema, Description: description);

    private static readonly NodeDescriptor[] Nodes =
    [
        Node("core.manualTrigger", 1, NodeGroup.Trigger, [], description: "Ручний запуск. Друге речення."),
        Node("core.if", 1, NodeGroup.Logic, ["main"], ["true", "false"], "Умова: розгалужує потік."),
        Node("core.approval", 1, NodeGroup.Logic, ["main"], ["approved", "rejected"]),
        Node("core.wait", 1, NodeGroup.Logic, ["main"]),
        Node("core.wait", 3, NodeGroup.Logic, ["main"], ["main", "timeout"], "Очікування."),
        Node("core.set", 1, NodeGroup.Transform, ["main"]),
        Node("mcp.tool", 1, NodeGroup.Integration, ["main"]),
        Node("vendor.custom", 1, NodeGroup.Integration, ["main"]) with { PlanKind = "notify" },
    ];

    [Fact]
    public async Task Digest_has_latest_version_of_each_type_with_kind_and_outputs_but_no_schemas()
    {
        var digest = await new CatalogDigestBuilder(new FakeNodes(Nodes), Connectors()).BuildAsync();

        Assert.Equal(7, digest.Nodes.Count);
        var wait = Assert.Single(digest.Nodes, n => n.Type == "core.wait");
        Assert.Equal(3, wait.Version);
        Assert.Equal(["main", "timeout"], wait.Outputs);
        Assert.Equal("wait", wait.Kind);
        Assert.Equal("trigger", digest.Nodes.Single(n => n.Type == "core.manualTrigger").Kind);
        Assert.Equal("Ручний запуск.", digest.Nodes.Single(n => n.Type == "core.manualTrigger").Summary);
        Assert.Equal("condition", digest.Nodes.Single(n => n.Type == "core.if").Kind);
        Assert.Equal("approval", digest.Nodes.Single(n => n.Type == "core.approval").Kind);
        Assert.Equal("transform", digest.Nodes.Single(n => n.Type == "core.set").Kind);
        Assert.Equal("action", digest.Nodes.Single(n => n.Type == "mcp.tool").Kind);
        Assert.Equal("notify", digest.Nodes.Single(n => n.Type == "vendor.custom").Kind);
        Assert.DoesNotContain("xxxx", Json(digest), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Digest_lists_only_tools_of_connected_connectors()
    {
        var ok = Guid.NewGuid();
        var broken = Guid.NewGuid();
        ConnectorSummary[] summaries =
        [
            new(ok, "Telegram", McpTransportKind.Http, ConnectorStatus.Ok, 1, null),
            new(broken, "Зламаний", McpTransportKind.Http, ConnectorStatus.Error, 1, null),
            new(Guid.NewGuid(), "Новий", McpTransportKind.Http, ConnectorStatus.New, 1, null),
        ];
        var tools = new Dictionary<Guid, McpToolInfo[]>
        {
            [ok] = [new("send_message", "Надіслати повідомлення", "Надсилає повідомлення в чат.\nДеталі.", new JsonObject())],
            [broken] = [new("lost", null, null, new JsonObject())],
        };

        var digest = await new CatalogDigestBuilder(new FakeNodes(Nodes), Connectors(tools, summaries)).BuildAsync();

        var action = Assert.Single(digest.Actions);
        Assert.Equal($"mcp:{ok}/send_message", action.Use);
        Assert.Equal("Telegram", action.Connector);
        Assert.Equal("Надіслати повідомлення", action.Title);
        Assert.Equal("Надсилає повідомлення в чат.", action.Summary);
        Assert.False(digest.Truncated);
    }

    [Fact]
    public async Task Digest_fits_the_budget_even_with_many_connector_tools()
    {
        var id = Guid.NewGuid();
        var many = Enumerable.Range(1, 200)
            .Select(i => new McpToolInfo($"tool_{i}", $"Інструмент {i}", new string('о', 300), new JsonObject()))
            .ToArray();
        var connectors = Connectors(
            new Dictionary<Guid, McpToolInfo[]> { [id] = many },
            new ConnectorSummary(id, "Велика система", McpTransportKind.Http, ConnectorStatus.Ok, many.Length, null));

        var digest = await new CatalogDigestBuilder(new FakeNodes(Nodes), connectors).BuildAsync();

        Assert.True(Json(digest).Length <= ICatalogDigest.MaxChars, $"{Json(digest).Length} символів");
        Assert.True(digest.Truncated);
        Assert.NotEmpty(digest.Actions);
        Assert.Equal(7, digest.Nodes.Count);
    }

    private static string Json(CatalogDigest digest) => JsonSerializer.Serialize(digest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static IConnectorService Connectors(params ConnectorSummary[] summaries) => Connectors([], summaries);

    private static IConnectorService Connectors(Dictionary<Guid, McpToolInfo[]> tools, params ConnectorSummary[] summaries) =>
        Stub.Create(summaries, tools);

    private sealed class FakeNodes(IReadOnlyList<NodeDescriptor> descriptors) : INodeService
    {
        public Task<IReadOnlyList<NodeDescriptor>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(descriptors);
    }

    /// <summary>Заглушка <see cref="IConnectorService"/>: лише <c>ListAsync</c> і <c>GetAsync</c>.</summary>
    public class Stub : DispatchProxy
    {
        private ConnectorSummary[] summaries = [];
        private Dictionary<Guid, McpToolInfo[]> tools = [];

        public static IConnectorService Create(ConnectorSummary[] summaries, Dictionary<Guid, McpToolInfo[]> tools)
        {
            var proxy = Create<IConnectorService, Stub>();
            var stub = (Stub)(object)proxy;
            stub.summaries = summaries;
            stub.tools = tools;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            object value = targetMethod!.Name switch
            {
                nameof(IConnectorService.ListAsync) => new PageResult<ConnectorSummary>(summaries, 1, summaries.Length, summaries.Length),
                nameof(IConnectorService.GetAsync) => Detail((Guid)args![0]!),
                _ => throw new NotSupportedException(targetMethod.Name),
            };
            var result = targetMethod.ReturnType.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
        }

        private ConnectorDetail Detail(Guid id) =>
            new(summaries.Single(s => s.Id == id), new ConnectorConfig(), null, tools.GetValueOrDefault(id) ?? []);
    }
}
