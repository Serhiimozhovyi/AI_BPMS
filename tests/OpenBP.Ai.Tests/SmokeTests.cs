namespace OpenBP.Ai.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Ai", OpenBP.Ai.AssemblyMarker.Name);
    }
}
