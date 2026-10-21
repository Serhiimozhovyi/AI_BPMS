namespace OpenBP.Engine.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Engine", OpenBP.Engine.AssemblyMarker.Name);
    }
}
