namespace OpenBP.Worker.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Worker", OpenBP.Worker.AssemblyMarker.Name);
    }
}
