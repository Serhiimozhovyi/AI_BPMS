using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;

namespace OpenBP.Engine.Tests;

public class NodeDescriptorTests
{
    [Fact]
    public void IsTrigger_WhenNoInputs()
    {
        var trigger = new NodeDescriptor("core.webhook", 1, "Webhook", NodeGroup.Trigger, [], ["main"], new JsonObject());
        var logic = new NodeDescriptor("core.if", 1, "Умова", NodeGroup.Logic, ["main"], ["true", "false"], new JsonObject());

        Assert.True(trigger.IsTrigger);
        Assert.False(logic.IsTrigger);
    }

    [Fact]
    public void CredentialRequirement_DefaultsToCredentialIdParameter()
    {
        var requirement = new CredentialRequirement("anthropic", "anthropic");

        Assert.Equal("credentialId", requirement.Parameter);
        Assert.True(requirement.Required);
    }

    [Fact]
    public void Exceptions_CarryWhatWasRequested()
    {
        var node = new NodeTypeNotFoundException("core.nope", 3);
        var credential = new CredentialNotFoundException("cred_x");
        var binary = new BinaryNotFoundException("file_1");

        Assert.Equal(("core.nope", 3), (node.Type, node.TypeVersion));
        Assert.Equal("cred_x", credential.CredentialId);
        Assert.Equal("file_1", binary.Id);
        Assert.All(new EngineException[] { node, credential, binary }, e => Assert.NotEmpty(e.Message));
    }
}
