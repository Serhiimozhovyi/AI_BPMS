namespace OpenBP.Nodes.Core.Tests;

public class LoopNodeTests
{
    private readonly LoopNode _node = new();

    [Fact]
    public async Task Expands_list_into_items()
    {
        var ctx = Ctx.For(_node, """{"list":[{"sku":"A"},{"sku":"B"},7]}""", """{"order":1}""");

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"sku":"A"}""", """{"sku":"B"}""", """{"value":7}"""], Ctx.Json(Assert.Single(output)));
    }

    [Fact]
    public async Task Batches_by_size()
    {
        var ctx = Ctx.For(_node, """{"list":[1,2,3,4,5],"batchSize":2}""", "{}");

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(["""{"batch":[1,2]}""", """{"batch":[3,4]}""", """{"batch":[5]}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Non_array_list_fails()
    {
        var ctx = Ctx.For(_node, """{"list":"не масив"}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Zero_batch_size_fails()
    {
        var ctx = Ctx.For(_node, """{"list":[1],"batchSize":0}""", "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
