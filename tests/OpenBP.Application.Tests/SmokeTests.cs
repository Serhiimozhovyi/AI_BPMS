namespace OpenBP.Application.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Application", OpenBP.Application.AssemblyMarker.Name);
    }
}
