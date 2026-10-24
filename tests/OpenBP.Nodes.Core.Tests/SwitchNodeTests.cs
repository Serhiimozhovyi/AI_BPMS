using System.Text.Json.Nodes;

namespace OpenBP.Nodes.Core.Tests;

public class SwitchNodeTests
{
    private readonly SwitchNode _node = new(1);

    [Fact]
    public async Task V1_routes_to_first_matching_rule_or_fallback()
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
    public async Task V1_more_than_4_rules_fail()
    {
        var rule = """{"conditions":[]}""";
        var ctx = Ctx.For(_node, $$"""{"rules":[{{string.Join(",", Enumerable.Repeat(rule, 5))}}]}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public void Descriptors_v1_keeps_old_output_indexes_v2_puts_fallback_first()
    {
        Assert.Equal(["rule0", "rule1", "rule2", "rule3", "fallback"], new SwitchNode(1).Descriptor.Outputs);
        var v2 = new SwitchNode(2).Descriptor;
        Assert.Equal(2, v2.Version);
        Assert.Equal(17, v2.Outputs.Count);
        Assert.Equal("fallback", v2.Outputs[0]);
        Assert.Equal("rule15", v2.Outputs[16]);
        Assert.Equal(16, v2.ParametersSchema["properties"]!["rules"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(4, new SwitchNode(1).Descriptor.ParametersSchema["properties"]!["rules"]!["maxItems"]!.GetValue<int>());
    }

    [Fact]
    public async Task V2_routes_seven_rules_and_fallback()
    {
        // Як маршрутизатор n8n-бота: 7 гілок за типом оновлення.
        string[] kinds = ["start", "contact", "menu", "book", "cancel", "event", "sos"];
        var node = new SwitchNode(2);
        var items = kinds.Append("???").Append("sos").Select(k => $$"""{"k":"{{k}}"}""").ToArray();
        var ctx = Ctx.For(node, "{}", items);
        ctx.ParametersForItem = i =>
        {
            var k = ctx.Items()[i].Json["k"]!.ToJsonString();
            var rules = string.Join(",", kinds.Select(kind => $$"""{"conditions":[{"left":{{k}},"operator":"equals","right":"{{kind}}"}]}"""));
            return (JsonObject)JsonNode.Parse($$"""{"rules":[{{rules}}]}""")!;
        };

        var output = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(17, output.Length);
        Assert.Equal(["""{"k":"???"}"""], Ctx.Json(output[0]));
        for (var r = 0; r < kinds.Length - 1; r++)
        {
            Assert.Equal([$$"""{"k":"{{kinds[r]}}"}"""], Ctx.Json(output[r + 1]));
        }

        Assert.Equal(["""{"k":"sos"}""", """{"k":"sos"}"""], Ctx.Json(output[7]));
        Assert.All(output.Skip(8), o => Assert.Empty(o));
    }

    [Fact]
    public async Task V2_16_rules_pass_17_fail()
    {
        var node = new SwitchNode(2);
        var rule = """{"conditions":[{"left":1,"operator":"equals","right":2}]}""";
        var ok = Ctx.For(node, $$"""{"rules":[{{string.Join(",", Enumerable.Repeat(rule, 15))}},{"conditions":[{"left":1,"operator":"equals","right":1}]}]}""", "{}");
        var output = await node.ExecuteAsync(ok, CancellationToken.None);
        Assert.Single(output[16]);

        var tooMany = Ctx.For(node, $$"""{"rules":[{{string.Join(",", Enumerable.Repeat(rule, 17))}}]}""", "{}");
        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(tooMany, CancellationToken.None));
        Assert.Contains("16", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_condition_fails()
    {
        var ctx = Ctx.For(_node, """{"rules":[{"conditions":[{"left":1,"operator":"nope"}]}]}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
