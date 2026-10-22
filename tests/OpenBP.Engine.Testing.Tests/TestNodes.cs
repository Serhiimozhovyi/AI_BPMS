using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

/// <summary>Вузол-заглушка для тестів реєстру і контексту: пропускає items на єдиний вихід.</summary>
internal sealed class PassThroughNode(string type = "test.pass", int version = 1) : INode
{
    public NodeDescriptor Descriptor { get; } = new(
        type, version, "Прохідний", NodeGroup.Transform, ["main"], ["main"], new JsonObject());

    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct) =>
        Task.FromResult<Item[][]>([[.. ctx.Items()]]);
}
