using System.Diagnostics;

namespace OpenBP.Nodes.Core.Tests;

public class WaitNodeTests
{
    private readonly WaitNode _node = new();

    [Fact]
    public async Task Waits_and_passes_items_through()
    {
        var ctx = Ctx.For(_node, """{"amount":50,"unit":"milliseconds"}""", """{"a":1}""", """{"a":2}""");
        var watch = Stopwatch.StartNew();

        var output = await _node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.True(watch.ElapsedMilliseconds >= 40, $"Минуло {watch.ElapsedMilliseconds} мс");
        Assert.Equal(["""{"a":1}""", """{"a":2}"""], Ctx.Json(output[0]));
    }

    [Fact]
    public async Task Cancellation_interrupts_wait()
    {
        var ctx = Ctx.For(_node, """{"amount":10,"unit":"minutes"}""", "{}");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _node.ExecuteAsync(ctx, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("""{"amount":-1}""")]
    [InlineData("""{"amount":2,"unit":"hours"}""")]
    [InlineData("""{"amount":1,"unit":"days"}""")]
    public async Task Invalid_duration_fails(string parameters)
    {
        var ctx = Ctx.For(_node, parameters, "{}");

        await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
    }
}
