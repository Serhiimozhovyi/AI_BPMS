using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;
using static OpenBP.Engine.Tests.Execution.TestGraph;

namespace OpenBP.Engine.Tests.Execution;

/// <summary>F3-57: помилка кроку — «що сталося → що зробити» у message, технічне — у details, item — у itemIndex.</summary>
public sealed class NodeErrorTests
{
    [Fact]
    public async Task Unexpected_exception_text_goes_to_details()
    {
        var g = new TestGraph().Node("t", Trigger).Node("f", Fail).Edge("t", "f");

        var result = await g.RunAsync();

        var error = result.StepOf("f")!.Error!;
        Assert.DoesNotContain("Зламано", error.Message, StringComparison.Ordinal);
        Assert.Contains("Повторіть запуск", error.Message, StringComparison.Ordinal);
        Assert.Equal("Зламано", error.Details);
        Assert.Null(error.ItemIndex);
    }

    [Fact]
    public async Task Engine_exception_keeps_message_details_and_item()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.item", ["main"], ["main"]), (_, _) =>
            throw new EngineException("Поле «x» порожнє [item 1]. Вкажіть значення.") { Details = "raw", ItemIndex = 1 }));
        g.Node("t", Trigger, new JsonObject { ["count"] = 2 }).Node("i", "test.item").Edge("t", "i");

        var result = await g.RunAsync();

        var error = result.StepOf("i")!.Error!;
        Assert.Equal("Поле «x» порожнє [item 1]. Вкажіть значення.", error.Message);
        Assert.Equal("raw", error.Details);
        Assert.Equal(1, error.ItemIndex);
    }

    [Fact]
    public async Task Error_port_item_carries_details_and_item_index()
    {
        var g = new TestGraph();
        g.Registry.Register(new FuncNode(Descriptor("test.item", ["main"], ["main"]), (_, _) =>
            throw new EngineException("Не вийшло. Спробуйте ще.") { Details = "raw", ItemIndex = 0 }));
        g.Node("t", Trigger).Node("i", "test.item", configure: n => n.ContinueOnFail = true).Node("onError", Pass)
            .Edge("t", "i").Edge("i", "onError", output: 1);

        var result = await g.RunAsync();

        var error = result.OutputOf("onError")[0].Json["error"]!;
        Assert.Equal("raw", error["details"]!.GetValue<string>());
        Assert.Equal(0, error["itemIndex"]!.GetValue<int>());
    }

    [Fact]
    public async Task Failing_expression_names_the_parameter_and_item()
    {
        var g = new TestGraph();
        g.Expressions.Fallback = (expression, scope) => scope.ItemIndex == 1
            ? throw new ExpressionException(expression, "Вираз не виконався: $json.x is undefined.")
            : JsonValue.Create(1);
        g.Registry.Register(new FuncNode(Descriptor("test.params", ["main"], ["main"]), (ctx, _) =>
        {
            for (var i = 0; i < ctx.Items().Count; i++)
            {
                ctx.Params<JsonObject>(i);
            }

            return Task.FromResult<Item[][]>([[.. ctx.Items()]]);
        }));
        g.Node("t", Trigger, new JsonObject { ["count"] = 2 })
            .Node("p", "test.params", new JsonObject { ["body"] = new JsonObject { ["rows"] = new JsonArray("ok", "{{ $json.x.y }}") } })
            .Edge("t", "p");

        var result = await g.RunAsync();

        var error = result.StepOf("p")!.Error!;
        Assert.StartsWith("Вираз у параметрі «body.rows[1]» не обчислився [item 1]", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("Перевірте вираз у редакторі виразів на вхідних даних.", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, error.ItemIndex);
        Assert.Contains("$json.x is undefined", error.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Поле порожнє. Вкажіть значення.", 2, 3, "Поле порожнє [item 2]. Вкажіть значення.")]
    [InlineData("Поле порожнє.", 0, 2, "Поле порожнє [item 0].")]
    [InlineData("Поле порожнє. Вкажіть значення.", 0, 1, "Поле порожнє. Вкажіть значення.")]
    [InlineData("Поле порожнє [item 1]. Вкажіть значення.", 2, 3, "Поле порожнє [item 1]. Вкажіть значення.")]
    public void AtItem_marks_the_first_sentence(string message, int index, int count, string expected) =>
        Assert.Equal(expected, EngineException.AtItem(message, index, count));
}
