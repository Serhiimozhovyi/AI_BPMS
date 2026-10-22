using OpenBP.Engine;
using OpenBP.Nodes.Core;

namespace OpenBP.Worker.Tests;

public sealed class WorkerUnitTests
{
    [Fact]
    public void NodeRegistry_ResolvesRegistered_RejectsDuplicates()
    {
        var registry = new WorkerNodeRegistry(CoreNodes.All());

        Assert.Equal("core.set", registry.Resolve("core.set", 1).Descriptor.Type);
        Assert.False(registry.TryResolve("core.set", 99, out _));
        Assert.Throws<NodeTypeNotFoundException>(() => registry.Resolve("core.nope", 1));
        Assert.Throws<InvalidOperationException>(() => new WorkerNodeRegistry(CoreNodes.All().Concat(CoreNodes.All())));
    }

    [Fact]
    public void Options_DefaultsMatchArchitecture_AndAreValidated()
    {
        var options = new WorkerOptions();
        options.Validate();
        Assert.Equal(50, options.MaxConcurrentExecutions);
        Assert.Equal(TimeSpan.FromSeconds(5), options.HeartbeatInterval);
        Assert.False(string.IsNullOrWhiteSpace(options.WorkerId));
        Assert.NotEqual(options.WorkerId, new WorkerOptions().WorkerId);

        Assert.ThrowsAny<ArgumentException>(() => new WorkerOptions { MaxConcurrentExecutions = 0 }.Validate());
        Assert.ThrowsAny<ArgumentException>(() => new WorkerOptions { HeartbeatInterval = TimeSpan.Zero }.Validate());
        Assert.ThrowsAny<ArgumentException>(() => new WorkerOptions { WorkerId = " " }.Validate());
    }
}
