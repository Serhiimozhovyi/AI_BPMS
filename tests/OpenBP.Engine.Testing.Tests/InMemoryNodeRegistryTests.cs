namespace OpenBP.Engine.Testing.Tests;

public class InMemoryNodeRegistryTests
{
    [Fact]
    public void Resolve_ReturnsRegisteredNode_ByTypeAndVersion()
    {
        var v1 = new PassThroughNode(version: 1);
        var v2 = new PassThroughNode(version: 2);
        var registry = new InMemoryNodeRegistry().Register(v1).Register(v2);

        Assert.Same(v1, registry.Resolve("test.pass", 1));
        Assert.Same(v2, registry.Resolve("test.pass", 2));
        Assert.Equal(2, registry.Descriptors.Count);
    }

    [Fact]
    public void Resolve_Throws_ForUnknownTypeOrVersion()
    {
        var registry = new InMemoryNodeRegistry().Register(new PassThroughNode());

        var unknownType = Assert.Throws<NodeTypeNotFoundException>(() => registry.Resolve("test.other", 1));
        var unknownVersion = Assert.Throws<NodeTypeNotFoundException>(() => registry.Resolve("test.pass", 9));

        Assert.Equal("test.other", unknownType.Type);
        Assert.Equal(9, unknownVersion.TypeVersion);
    }

    [Fact]
    public void TryResolve_DoesNotThrow()
    {
        var registry = new InMemoryNodeRegistry().Register(new PassThroughNode());

        Assert.True(registry.TryResolve("test.pass", 1, out var found));
        Assert.NotNull(found);
        Assert.False(registry.TryResolve("test.pass", 2, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void Register_SameTypeAndVersionTwice_Throws()
    {
        var registry = new InMemoryNodeRegistry().Register(new PassThroughNode());

        Assert.Throws<InvalidOperationException>(() => registry.Register(new PassThroughNode()));
    }
}
