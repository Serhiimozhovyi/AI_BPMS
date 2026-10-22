namespace OpenBP.Nodes.Core.Tests;

public class ManualTriggerNodeTests
{
    [Fact]
    public async Task Emits_one_empty_item()
    {
        var node = new ManualTriggerNode();
        var output = await node.ExecuteAsync(Ctx.For(node, "{}"), CancellationToken.None);

        Assert.True(node.Descriptor.IsTrigger);
        var item = Assert.Single(Assert.Single(output));
        Assert.Empty(item.Json);
    }

    [Fact]
    public async Task Cancelled_token_throws()
    {
        var node = new ManualTriggerNode();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            node.ExecuteAsync(Ctx.For(node, "{}"), new CancellationToken(canceled: true)));
    }
}
