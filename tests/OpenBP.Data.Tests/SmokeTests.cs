namespace OpenBP.Data.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Data", OpenBP.Data.AssemblyMarker.Name);
    }
}
