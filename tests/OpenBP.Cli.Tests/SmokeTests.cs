namespace OpenBP.Cli.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("openbp", OpenBP.Cli.AssemblyMarker.Name);
    }
}
