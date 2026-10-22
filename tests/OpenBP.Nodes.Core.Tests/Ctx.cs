using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

internal static class Ctx
{
    public static FakeNodeContext For(INode node, string parameters, params string[] items)
    {
        var ctx = new FakeNodeContext
        {
            Node = new NodeDefinition { Id = "n1", Name = "Вузол", Type = node.Descriptor.Type },
            Parameters = (JsonObject)JsonNode.Parse(parameters)!,
        };
        return ctx.WithItems([.. items.Select(Item)]);
    }

    public static Item Item(string json) => new() { Json = (JsonObject)JsonNode.Parse(json)! };

    public static string[] Json(IEnumerable<Item> items) => [.. items.Select(i => i.Json.ToJsonString())];
}
