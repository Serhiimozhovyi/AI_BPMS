namespace OpenBP.Nodes.Core.Tests;

public class SetNodeTests
{
    private readonly SetNode _node = new();

    [Fact]
    public async Task Sets_fields_by_path_and_keeps_others()
    {
        var input = Ctx.Item("""{"a":1,"order":{"id":7}}""");
        var ctx = Ctx.For(_node, """{"fields":[{"name":"b","value":"x"},{"name":"order.customer","value":{"name":"Петренко"}},{"name":"deep.new.flag","value":true}]}""");
        ctx.WithItems(input);

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(
            """{"a":1,"order":{"id":7,"customer":{"name":"Петренко"}},"b":"x","deep":{"new":{"flag":true}}}""",
            Assert.Single(output[0]).Json.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal("""{"a":1,"order":{"id":7}}""", input.Json.ToJsonString());
    }

    [Fact]
    public async Task Keep_only_set_drops_other_fields()
    {
        var ctx = Ctx.For(_node, """{"keepOnlySet":true,"fields":[{"name":"b","value":2}]}""", """{"a":1}""", """{"a":2}""");

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"b":2}""", """{"b":2}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Path_through_non_object_fails()
    {
        var ctx = Ctx.For(_node, """{"fields":[{"name":"a.b","value":1}]}""", """{"a":5}""");

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("a.b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_field_name_fails()
    {
        var ctx = Ctx.For(_node, """{"fields":[{"name":"a..b","value":1}]}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
