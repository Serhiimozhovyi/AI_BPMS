using System.Text.Json.Nodes;

namespace OpenBP.Engine.Testing.Tests;

public class FakeCredentialProviderTests
{
    [Fact]
    public async Task GetAsync_ReturnsRegisteredData()
    {
        var provider = new FakeCredentialProvider()
            .Add("cred_1", new JsonObject { ["apiKey"] = "secret" });

        var data = await provider.GetAsync("cred_1", CancellationToken.None);

        Assert.Equal("secret", (string?)data["apiKey"]);
    }

    [Fact]
    public async Task GetAsync_ReturnsIndependentCopies()
    {
        var source = new JsonObject { ["apiKey"] = "secret" };
        var provider = new FakeCredentialProvider().Add("cred_1", source);
        source["apiKey"] = "changed-after-add";

        var first = await provider.GetAsync("cred_1", CancellationToken.None);
        first["apiKey"] = "mutated-by-node";
        var second = await provider.GetAsync("cred_1", CancellationToken.None);

        Assert.Equal("secret", (string?)second["apiKey"]);
    }

    [Fact]
    public async Task GetAsync_Throws_ForUnknownId()
    {
        var provider = new FakeCredentialProvider();

        var ex = await Assert.ThrowsAsync<CredentialNotFoundException>(() =>
            provider.GetAsync("missing", CancellationToken.None));

        Assert.Equal("missing", ex.CredentialId);
    }

    [Fact]
    public async Task Remove_MakesCredentialUnavailable()
    {
        var provider = new FakeCredentialProvider().Add("cred_1", new JsonObject());

        Assert.True(provider.Remove("cred_1"));
        Assert.False(provider.Remove("cred_1"));
        await Assert.ThrowsAsync<CredentialNotFoundException>(() => provider.GetAsync("cred_1", CancellationToken.None));
    }
}
