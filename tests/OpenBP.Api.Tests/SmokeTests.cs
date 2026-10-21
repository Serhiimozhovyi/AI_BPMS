namespace OpenBP.Api.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Api", OpenBP.Api.AssemblyMarker.Name);
    }
}
