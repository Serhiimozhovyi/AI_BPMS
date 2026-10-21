namespace OpenBP.Expressions.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.Equal("OpenBP.Expressions", OpenBP.Expressions.AssemblyMarker.Name);
    }
}
