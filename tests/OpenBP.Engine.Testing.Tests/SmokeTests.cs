namespace OpenBP.Engine.Testing.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Engine.Testing", OpenBP.Engine.Testing.AssemblyMarker.Name);
    }
}
