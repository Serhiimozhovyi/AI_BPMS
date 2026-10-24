using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>
/// F3-57: тексти помилок <c>core.*</c> — «що сталося → що зробити»: без стеку і слів «помилка»/«exception»,
/// з підказкою другим реченням; технічне — у <c>Details</c>, item — у <c>ItemIndex</c> і позначці «[item N]».
/// </summary>
public sealed class NodeErrorMessagesTests
{
    public static TheoryData<string> Cases => new()
    {
        "loop", "loopBatch", "set", "if", "switch", "merge", "code", "codeEmpty", "codeResult",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Message_explains_what_happened_and_what_to_do(string name)
    {
        var (node, ctx) = Case(name);

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        AssertHumanMessage(ex.Message);
    }

    [Fact]
    public async Task Failing_item_index_is_reported_when_there_are_several_items()
    {
        var node = new LoopNode();
        var ctx = Ctx.For(node, """{"list":"{{ $json.lines }}"}""", """{"lines":[1]}""", """{"lines":"x"}""");
        ctx.ParametersForItem = i => i == 0
            ? new JsonObject { ["list"] = new JsonArray(1) }
            : new JsonObject { ["list"] = "x" };

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.Equal(1, ex.ItemIndex);
        Assert.Contains("[item 1]", ex.Message, StringComparison.Ordinal);
        AssertHumanMessage(ex.Message);
    }

    [Fact]
    public async Task Single_item_has_no_item_label()
    {
        var node = new SetNode();
        var ctx = Ctx.For(node, """{"fields":[{"name":"a..b","value":1}]}""", "{}");

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.DoesNotContain("[item", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_engine_text_goes_to_details()
    {
        var node = new CodeNode();
        var ctx = Ctx.For(node, new JsonObject { ["code"] = "throw new TypeError('x is not a function');", ["mode"] = "eachItem" }.ToJsonString(), "{}");

        var ex = await Assert.ThrowsAsync<CoreNodeException>(() => node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.StartsWith("Код не виконався", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Виправте код вузла.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("x is not a function", ex.Details, StringComparison.Ordinal);
    }

    internal static void AssertHumanMessage(string message)
    {
        Assert.DoesNotContain("помилк", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exception", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', message);

        // Дві частини: що сталося. Що зробити.
        var sentences = message.Split(". ", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(sentences.Length >= 2, $"Немає підказки, що зробити: «{message}»");
        Assert.EndsWith(".", message, StringComparison.Ordinal);
    }

    private static (INode Node, Engine.Testing.FakeNodeContext Ctx) Case(string name)
    {
        INode node = name switch
        {
            "loop" or "loopBatch" => new LoopNode(),
            "set" => new SetNode(),
            "if" => new IfNode(),
            "switch" => new SwitchNode(1),
            "merge" => new MergeNode(),
            _ => new CodeNode(),
        };
        var rule = """{"conditions":[{"left":1,"operator":"equals","right":1}]}""";
        var parameters = name switch
        {
            "loop" => """{"list":"не масив"}""",
            "loopBatch" => """{"list":[1],"batchSize":0}""",
            "set" => """{"fields":[{"name":"a..b","value":1}]}""",
            "if" => """{"conditions":[{"left":1,"operator":"like","right":1}]}""",
            "switch" => $$"""{"rules":[{{string.Join(",", Enumerable.Repeat(rule, 5))}}]}""",
            "merge" => """{"mode":"zip"}""",
            "code" => new JsonObject { ["code"] = "throw new Error('бум');", ["mode"] = "eachItem" }.ToJsonString(),
            "codeEmpty" => """{"code":"   ","mode":"eachItem"}""",
            _ => """{"code":"return 42;","mode":"eachItem"}""",
        };
        return (node, Ctx.For(node, parameters, "{}"));
    }
}
