namespace OpenBP.Nodes.Core.Tests;

public class IfNodeTests
{
    private readonly IfNode _node = new();

    [Fact]
    public async Task Routes_each_item_by_its_own_parameters()
    {
        var ctx = Ctx.For(_node, "{}", """{"qty":5}""", """{"qty":1}""", """{"qty":10}""");
        // Параметри для кожного item'а — як після обчислення {{ $json.qty }} движком.
        ctx.ParametersForItem = i => new()
        {
            ["conditions"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["left"] = ctx.Items()[i].Json["qty"]!.DeepClone(), ["operator"] = "gte", ["right"] = 5 }),
        };

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, output.Length);
        Assert.Equal(["""{"qty":5}""", """{"qty":10}"""], Ctx.Json(output[0]));
        Assert.Equal(["""{"qty":1}"""], Ctx.Json(output[1]));
    }

    [Fact]
    public async Task Or_combination()
    {
        var ctx = Ctx.For(_node, """{"combine":"or","conditions":[{"left":"a","operator":"equals","right":"b"},{"left":"abc","operator":"startsWith","right":"ab"}]}""", "{}");

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(output[0]);
        Assert.Empty(output[1]);
    }

    [Fact]
    public async Task Unknown_operator_fails()
    {
        var ctx = Ctx.For(_node, """{"conditions":[{"left":1,"operator":"like","right":1}]}""", "{}");

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("like", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Numeric_operator_on_text_fails()
    {
        var ctx = Ctx.For(_node, """{"conditions":[{"left":"багато","operator":"gt","right":1}]}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
