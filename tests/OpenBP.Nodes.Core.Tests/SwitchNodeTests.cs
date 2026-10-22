using System.Text.Json.Nodes;

namespace OpenBP.Nodes.Core.Tests;

public class SwitchNodeTests
{
    private readonly SwitchNode _node = new();

    [Fact]
    public async Task Routes_to_first_matching_rule_or_fallback()
    {
        var ctx = Ctx.For(_node, "{}", """{"s":"new"}""", """{"s":"paid"}""", """{"s":"???"}""");
        // Параметри для кожного item'а — як після обчислення {{ $json.s }} движком.
        ctx.ParametersForItem = i =>
        {
            var s = ctx.Items()[i].Json["s"]!.ToJsonString();
            return (JsonObject)JsonNode.Parse($$"""
                {"rules":[
                  {"conditions":[{"left":{{s}},"operator":"equals","right":"new"}]},
                  {"conditions":[{"left":{{s}},"operator":"equals","right":"paid"}]},
                  {"combine":"or","conditions":[{"left":{{s}},"operator":"equals","right":"new"},{"left":{{s}},"operator":"equals","right":"paid"}]}
                ]}
                """)!;
        };

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(5, output.Length);
        Assert.Equal(["""{"s":"new"}"""], Ctx.Json(output[0]));
        Assert.Equal(["""{"s":"paid"}"""], Ctx.Json(output[1]));
        Assert.Empty(output[2]);
        Assert.Empty(output[3]);
        Assert.Equal(["""{"s":"???"}"""], Ctx.Json(output[4]));
    }

    [Fact]
    public async Task Too_many_rules_fail()
    {
        var rule = """{"conditions":[]}""";
        var ctx = Ctx.For(_node, $$"""{"rules":[{{string.Join(",", Enumerable.Repeat(rule, 5))}}]}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_condition_fails()
    {
        var ctx = Ctx.For(_node, """{"rules":[{"conditions":[{"left":1,"operator":"nope"}]}]}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
