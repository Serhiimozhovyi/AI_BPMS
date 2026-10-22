using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

public class FakeNodeContextTests
{
    private sealed record SetParams(string Field, int Value);

    [Fact]
    public void Items_ReturnsConfiguredInputs_AndEmptyForMissingPort()
    {
        var ctx = new FakeNodeContext()
            .WithJson(new { a = 1 }, new { a = 2 })
            .WithInput(2, [new Item()]);

        Assert.Equal(2, ctx.Items().Count);
        Assert.Equal(2, (int?)ctx.Items()[1].Json["a"]);
        Assert.Empty(ctx.Items(1));
        Assert.Single(ctx.Items(2));
        Assert.Empty(ctx.Items(5));
    }

    [Fact]
    public void Params_DeserializesFromNodeParameters_ByDefault()
    {
        var ctx = new FakeNodeContext
        {
            Node = new NodeDefinition
            {
                Id = "n_set",
                Name = "Set",
                Type = "core.set",
                Parameters = new JsonObject { ["field"] = "x", ["value"] = 7 },
            },
        };

        var p = ctx.Params<SetParams>(0);

        Assert.Equal(new SetParams("x", 7), p);
    }

    [Fact]
    public void Params_PrefersExplicitParameters_ThenPerItem()
    {
        var ctx = new FakeNodeContext().WithParameters(new { field = "y", value = 1 });

        Assert.Equal("y", ctx.Params<SetParams>(0).Field);

        ctx.ParametersForItem = i => new JsonObject { ["field"] = $"item{i}", ["value"] = i };

        Assert.Equal("item3", ctx.Params<SetParams>(3).Field);
        Assert.Equal(3, ctx.Params<SetParams>(3).Value);
    }

    [Fact]
    public void Params_Throws_WhenShapeDoesNotMatch()
    {
        var ctx = new FakeNodeContext().WithParameters(new { value = "не число" });

        Assert.ThrowsAny<JsonException>(() => ctx.Params<SetParams>(0));
    }

    [Fact]
    public async Task GetCredentialAsync_ReturnsCopy_OrThrows()
    {
        var ctx = new FakeNodeContext().WithCredential("api", new JsonObject { ["token"] = "t" });

        var data = await ctx.GetCredentialAsync("api");
        data["token"] = "changed";
        var again = await ctx.GetCredentialAsync("api");

        Assert.Equal("t", (string?)again["token"]);
        await Assert.ThrowsAsync<CredentialNotFoundException>(() => ctx.GetCredentialAsync("other"));
    }

    [Fact]
    public async Task ExposesLoggerBinaryAndState_ThroughInterface()
    {
        INodeContext ctx = new FakeNodeContext();
        var fake = (FakeNodeContext)ctx;

        ctx.Logger.Log(LogLevel.Info, ctx.Node.Id, "привіт");
        using var content = new MemoryStream([1]);
        var reference = await ctx.Binary.PutAsync(content, "application/octet-stream", null, CancellationToken.None);
        ctx.NodeState["seenKeys"] = new JsonArray("k1");

        Assert.Single(fake.Logger.Entries);
        Assert.True(fake.Binary.Contains(reference.Id));
        Assert.Equal("k1", (string?)fake.NodeState["seenKeys"]![0]);
        Assert.NotEqual(Guid.Empty, ctx.ExecutionId);
    }

    [Fact]
    public async Task WorksWithRealNode_EndToEnd()
    {
        var node = new PassThroughNode();
        var ctx = new FakeNodeContext().WithJson(new { id = 1 }, new { id = 2 });

        var outputs = await node.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(outputs);
        Assert.Equal([1, 2], outputs[0].Select(i => (int?)i.Json["id"]));
    }
}
