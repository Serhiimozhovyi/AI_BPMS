namespace OpenBP.Shared.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Shared", OpenBP.Shared.AssemblyMarker.Name);
    }
}
