namespace OpenBP.Nodes.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Nodes.Core", OpenBP.Nodes.Core.AssemblyMarker.Name);
    }
}
