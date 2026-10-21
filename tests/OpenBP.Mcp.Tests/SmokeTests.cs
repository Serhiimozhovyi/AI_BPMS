namespace OpenBP.Mcp.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Mcp", OpenBP.Mcp.AssemblyMarker.Name);
    }
}
