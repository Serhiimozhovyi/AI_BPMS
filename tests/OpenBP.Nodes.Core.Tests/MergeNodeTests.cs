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

    // ---------- версія 2 (F7-06) ----------

    private readonly MergeNode _v2 = new(2);

    private static NodeDefinition Definition(string parameters) => new()
    {
        Id = "m",
        Name = "Злиття",
        Type = "core.merge",
        TypeVersion = 2,
        Parameters = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(parameters)!,
    };

    [Fact]
    public void V2_has_eight_inputs_and_v1_keeps_two()
    {
        Assert.Equal(8, _v2.Descriptor.Inputs.Count);
        Assert.Equal("input1", _v2.Descriptor.Inputs[0]);
        Assert.Equal(2, _node.Descriptor.Inputs.Count);
        Assert.Equal(OpenBP.Engine.Nodes.JoinRule.All, _node.GetJoinRule(Definition("""{"mode":"append"}""")));
    }

    [Theory]
    [InlineData("{}", null)]
    [InlineData("""{"mode":"all"}""", null)]
    [InlineData("""{"mode":"any"}""", 1)]
    [InlineData("""{"mode":"count","count":2}""", 2)]
    public void V2_join_rule_follows_mode(string parameters, int? required)
    {
        Assert.Equal(required, _v2.GetJoinRule(Definition(parameters)).Required);
    }

    [Theory]
    [InlineData("""{"mode":"count"}""")]
    [InlineData("""{"mode":"count","count":0}""")]
    [InlineData("""{"mode":"first"}""")]
    public void V2_invalid_join_parameters_fail(string parameters)
    {
        Assert.Throws<CoreNodeException>(() => _v2.GetJoinRule(Definition(parameters)));
    }

    [Fact]
    public async Task V2_append_takes_inputs_with_data_in_port_order()
    {
        var ctx = Ctx.For(_v2, """{"mode":"count","count":2}""", """{"a":1}""");
        ctx.WithInput(2, [Ctx.Item("""{"c":1}""")]);

        var output = await _v2.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"a":1}""", """{"c":1}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task V2_combine_by_position_merges_three_inputs()
    {
        var ctx = Ctx.For(_v2, """{"combine":"combineByPosition"}""", """{"id":1,"x":"a"}""");
        ctx.WithInput(1, [Ctx.Item("""{"x":"b"}""")]);
        ctx.WithInput(2, [Ctx.Item("""{"y":3}""")]);

        var output = await _v2.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("""{"id":1,"x":"b","y":3}""", Assert.Single(output[0]).Json.ToJsonString());
    }

    [Fact]
    public async Task V2_unknown_combine_fails()
    {
        var ctx = Ctx.For(_v2, """{"combine":"zip"}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _v2.ExecuteAsync(ctx, CancellationToken.None));
    }
}
