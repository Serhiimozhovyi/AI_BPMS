using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Engine.Testing;
using OpenBP.Engine.Validation;
using OpenBP.Shared.Validation;

namespace OpenBP.Nodes.Core.Tests;

public class CoreNodesTests
{
    public static TheoryData<string, string> ValidParameters => new()
    {
        { "core.manualTrigger", "{}" },
        { "core.webhook", """{"path":"telegram-in"}""" },
        { "core.webhook", """{"method":"GET","path":"orders/new","auth":{"type":"header","credentialId":"cred_1","headerName":"X-Token"}}""" },
        { "core.webhook", """{"method":"POST","path":"a.b~c_d-1","auth":{"type":"basic","credentialId":"cred_1"}}""" },
        { "core.schedule", """{"cron":"0 7 * * 1-5"}""" },
        { "core.schedule", """{"cron":"0 0 7 ? * MON-FRI","timezone":"Europe/Kyiv"}""" },
        { "core.set", """{"fields":[{"name":"order.customer","value":"{{ $json.name }}"}],"keepOnlySet":false}""" },
        { "core.if", """{"combine":"and","conditions":[{"left":"{{ $json.qty }}","operator":"gt","right":5}]}""" },
        { "core.switch", """{"rules":[{"conditions":[{"left":"{{ $json.s }}","operator":"equals","right":"new"}]}]}""" },
        { "core.merge", """{"mode":"combineByPosition"}""" },
        { "core.loop", """{"list":"{{ $json.lines }}","batchSize":1}""" },
        { "core.wait", """{"amount":5,"unit":"seconds"}""" },
        { "core.code", """{"code":"return $json;","mode":"eachItem"}""" },
    };

    public static TheoryData<string, string> InvalidParameters => new()
    {
        { "core.manualTrigger", """{"x":1}""" },
        { "core.webhook", "{}" },
        { "core.webhook", """{"path":"/leading-slash"}""" },
        { "core.webhook", """{"path":"a b"}""" },
        { "core.webhook", """{"path":"x","method":"OPTIONS"}""" },
        { "core.webhook", """{"path":"x","auth":{"type":"jwt","credentialId":"c"}}""" },
        { "core.webhook", """{"path":"x","auth":{"type":"header"}}""" },
        { "core.schedule", "{}" },
        { "core.schedule", """{"cron":""}""" },
        { "core.schedule", """{"cron":"0 7 * * *","timezone":""}""" },
        { "core.schedule", """{"cron":"0 7 * * *","every":"day"}""" },
        { "core.set", """{"fields":[{"name":""}]}""" },
        { "core.if", """{"conditions":[{"left":1,"operator":"like"}]}""" },
        { "core.switch", """{"rules":[]}""" },
        { "core.merge", """{"mode":"zip"}""" },
        { "core.loop", """{"list":5}""" },
        { "core.wait", """{"amount":-1}""" },
        { "core.code", """{"code":""}""" },
    };

    [Fact]
    public void All_core_nodes_register_and_resolve()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        string[] expected = ["core.manualTrigger", "core.webhook", "core.schedule", "core.set", "core.if", "core.switch", "core.merge", "core.loop", "core.wait", "core.code"];
        Assert.Equal(expected.Order(), registry.Descriptors.Select(d => d.Type).Distinct().Order());
        foreach (var type in expected)
        {
            var node = registry.Resolve(type, 1);
            Assert.Equal(type, node.Descriptor.Type);
            Assert.NotEmpty(node.Descriptor.Outputs);
            Assert.False(string.IsNullOrWhiteSpace(node.Descriptor.DisplayName));
            Assert.Equal("object", node.Descriptor.ParametersSchema["type"]!.GetValue<string>());
        }

        Assert.True(registry.Resolve("core.manualTrigger", 1).Descriptor.IsTrigger);
        Assert.True(registry.Resolve("core.webhook", 1).Descriptor.IsTrigger);
        Assert.True(registry.Resolve("core.schedule", 1).Descriptor.IsTrigger);
        Assert.Equal(2, registry.Resolve("core.merge", 1).Descriptor.Inputs.Count);
        Assert.Equal(5, registry.Resolve("core.switch", 1).Descriptor.Outputs.Count);
        Assert.Equal(17, registry.Resolve("core.switch", 2).Descriptor.Outputs.Count);
    }

    [Theory]
    [MemberData(nameof(ValidParameters))]
    public void Schema_accepts_valid_parameters(string type, string parameters)
    {
        var errors = Validate(type, parameters);

        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(InvalidParameters))]
    public void Schema_rejects_invalid_parameters(string type, string parameters)
    {
        var errors = Validate(type, parameters);

        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.Equal(WorkflowValidationCodes.ParameterInvalid, e.Code));
    }

    private static IReadOnlyList<WorkflowValidationError> Validate(string type, string parameters)
    {
        // Найновіша версія типу — та, яку додає палітра.
        var node = CoreNodes.All().Where(n => n.Descriptor.Type == type).MaxBy(n => n.Descriptor.Version)!;
        return ParameterSchemaValidator.Validate(node.Descriptor.ParametersSchema, JsonNode.Parse(parameters), "n1", "/parameters");
    }
}
