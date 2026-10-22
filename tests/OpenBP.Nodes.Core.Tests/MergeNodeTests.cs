using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

public class MergeNodeTests
{
    private readonly MergeNode _node = new();

    [Fact]
    public async Task Append_concatenates_inputs_in_order()
    {
        var ctx = Ctx.For(_node, """{"mode":"append"}""", """{"a":1}""");
        ctx.WithInput(1, [Ctx.Item("""{"b":1}"""), Ctx.Item("""{"b":2}""")]);

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"a":1}""", """{"b":1}""", """{"b":2}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Combine_by_position_merges_json_and_binary()
    {
        var left = Ctx.Item("""{"id":1,"x":"old"}""");
        var right = Ctx.Item("""{"x":"new","y":2}""");
        right.Binary = new() { ["file"] = new BinaryRef { Id = "f1", MimeType = "application/pdf" } };
        var ctx = Ctx.For(_node, """{"mode":"combineByPosition"}""").WithItems(left);
        ctx.WithInput(1, [right]);

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        var item = Assert.Single(output[0]);
        Assert.Equal("""{"id":1,"x":"new","y":2}""", item.Json.ToJsonString());
        Assert.Equal("f1", item.Binary!["file"].Id);
        Assert.Equal("""{"id":1,"x":"old"}""", left.Json.ToJsonString());
    }

    [Fact]
    public async Task Combine_by_position_with_different_counts_fails()
    {
        var ctx = Ctx.For(_node, """{"mode":"combineByPosition"}""", "{}", "{}");
        ctx.WithInput(1, [Ctx.Item("{}")]);

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_mode_fails()
    {
        var ctx = Ctx.For(_node, """{"mode":"zip"}""", "{}");
        ctx.WithInput(1, [Ctx.Item("{}")]);

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
