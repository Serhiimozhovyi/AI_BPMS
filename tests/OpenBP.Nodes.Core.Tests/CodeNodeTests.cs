using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

public class CodeNodeTests
{
    private readonly CodeNode _node = new();

    private static string Params(string code, string mode = "eachItem") =>
        new JsonObject { ["code"] = code, ["mode"] = mode }.ToJsonString();

    [Fact]
    public async Task Each_item_transforms_json_and_keeps_binary()
    {
        var item = Ctx.Item("""{"qty":2,"price":5}""");
        item.Binary = new() { ["doc"] = new BinaryRef { Id = "b1" } };
        var ctx = Ctx.For(_node, Params("return { total: $json.qty * $json.price, index: $index };")).WithItems(item);

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        var result = Assert.Single(output[0]);
        Assert.Equal("""{"total":10,"index":0}""", result.Json.ToJsonString());
        Assert.Equal("b1", result.Binary!["doc"].Id);
    }

    [Fact]
    public async Task Each_item_can_return_many_or_none()
    {
        var ctx = Ctx.For(_node, Params("return $json.n > 0 ? [{ n: $json.n }, { n: -$json.n }] : null;"), """{"n":1}""", """{"n":0}""");

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"n":1}""", """{"n":-1}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task All_items_runs_once()
    {
        var ctx = Ctx.For(_node, Params("return [{ sum: items.reduce((s, x) => s + x.n, 0), count: items.length }];", "allItems"), """{"n":1}""", """{"n":2}""");

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"sum":3,"count":2}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Each_item_reads_previous_node_output_with_the_same_index()
    {
        var ctx = Ctx.For(_node, Params("""return { order: $node["1С: замовлення"].json.number, chat: $node["Telegram"].json.chat, i: $index };"""), "{}", "{}");
        ctx.NodeOutputs["1С: замовлення"] = [Ctx.Item("""{"number":"0001"}"""), Ctx.Item("""{"number":"0002"}""")];
        ctx.NodeOutputs["Telegram"] = [Ctx.Item("""{"chat":42}""")]; // один item — для всіх індексів береться перший

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"order":"0001","chat":42,"i":0}""", """{"order":"0002","chat":42,"i":1}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task All_items_reads_first_item_and_all_items_of_previous_node()
    {
        var ctx = Ctx.For(_node, Params("""return [{ first: $node["Слоти"].json.id, count: $node["Слоти"].all.length, ids: $node["Слоти"].all.map(s => s.id) }];""", "allItems"), "{}");
        ctx.NodeOutputs["Слоти"] = [Ctx.Item("""{"id":"s1"}"""), Ctx.Item("""{"id":"s2"}""")];

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"first":"s1","count":2,"ids":["s1","s2"]}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Missing_node_fails_with_its_name()
    {
        var ctx = Ctx.For(_node, Params("""return { x: $node["Немає такого"].json.x };"""), "{}");
        ctx.NodeOutputs["Webhook"] = [Ctx.Item("{}")];

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("«Немає такого»", ex.Message, StringComparison.Ordinal);
        Assert.Contains("«Webhook»", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Previous_node_output_is_read_only()
    {
        var ctx = Ctx.For(_node, Params("""
            $node["A"].json.x = 99;
            $node["Нове"] = { json: {} };
            return { inCode: $node["A"].json.x, added: Object.keys($node).length };
            """), "{}");
        var source = Ctx.Item("""{"x":1}""");
        ctx.NodeOutputs["A"] = [source];

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("""{"x":1}""", source.Json.ToJsonString()); // вихід вузла A у движку не змінився
        Assert.Equal(1, output[0][0].Json["added"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("UTC", "2026-01-15T10:00:00Z", 10)]
    [InlineData("Europe/Kyiv", "2026-01-15T10:00:00Z", 12)] // зима: UTC+2
    [InlineData("Europe/Kyiv", "2026-07-15T10:00:00Z", 13)] // літо: UTC+3
    public async Task Date_uses_workflow_timezone(string timezone, string utc, int hours)
    {
        var ctx = Ctx.For(_node, Params($$"""return { h: new Date('{{utc}}').getHours(), offset: new Date('{{utc}}').getTimezoneOffset() };"""), "{}");
        ctx.Timezone = timezone;

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(hours, output[0][0].Json["h"]!.GetValue<int>());
        Assert.Equal((10 - hours) * 60, output[0][0].Json["offset"]!.GetValue<int>());
    }

    [Fact]
    public async Task Unknown_timezone_fails()
    {
        var ctx = Ctx.For(_node, Params("return {};"), "{}");
        ctx.Timezone = "Mars/Olympus";

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("Mars/Olympus", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("throw new Error('бум');")]
    [InlineData("return 42;")]
    [InlineData("return [1, 2];")]
    [InlineData("return {;")]
    [InlineData("   ")]
    public async Task Bad_code_or_result_fails(string code)
    {
        var ctx = Ctx.For(_node, Params(code), "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task All_items_must_return_array()
    {
        var ctx = Ctx.For(_node, Params("return { a: 1 };", "allItems"), "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Infinite_loop_hits_time_limit()
    {
        var node = new CodeNode(TimeSpan.FromMilliseconds(200));
        var ctx = Ctx.For(node, Params("while (true) {}"), "{}");
        var watch = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Contains("ліміт часу", ex.Message, StringComparison.Ordinal);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"Минуло {watch.Elapsed}");
    }

    [Fact]
    public void Default_time_limit_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), CodeNode.DefaultTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeNode(TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public async Task Cancellation_stops_running_code()
    {
        var ctx = Ctx.For(_node, Params("while (true) {}"), "{}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _node.ExecuteAsync(ctx, cts.Token));
    }

    [Theory]
    [InlineData("return { r: require('fs').readFileSync('/etc/passwd', 'utf8') };")]
    [InlineData("return { r: System.IO.File.ReadAllText('/etc/passwd') };")]
    [InlineData("return { r: importNamespace('System.IO').File.ReadAllText('/etc/passwd') };")]
    [InlineData("return { r: fetch('http://example.com') };")]
    [InlineData("return { r: new XMLHttpRequest() };")]
    [InlineData("return { r: process.env };")]
    [InlineData("return { r: Deno.readTextFile('/etc/passwd') };")]
    [InlineData("return { r: import('fs') };")]
    [InlineData("return { r: eval('1 + 1') };")]
    [InlineData("return { r: new Function('return 1')() };")]
    [InlineData("return { r: $json.constructor.constructor('return this')().System };")]
    public async Task Has_no_access_to_files_network_or_host(string code)
    {
        var ctx = Ctx.For(_node, Params(code), "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Memory_limit_is_enforced()
    {
        var ctx = Ctx.For(_node, Params("const a = []; while (true) { a.push('x'.repeat(1024 * 1024)); }"), "{}");

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.Contains("пам'яті", ex.Message, StringComparison.Ordinal);
    }
}
