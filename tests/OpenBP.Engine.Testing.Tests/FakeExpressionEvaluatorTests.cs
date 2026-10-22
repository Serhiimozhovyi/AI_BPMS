using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Testing.Tests;

public class FakeExpressionEvaluatorTests
{
    private static ExpressionScope Scope() => new()
    {
        Item = new Item { Json = new JsonObject { ["a"] = 1 } },
        ExecutionId = Guid.NewGuid(),
    };

    [Fact]
    public void Evaluate_PlainString_ReturnsItAsIs()
    {
        var evaluator = new FakeExpressionEvaluator();

        var result = evaluator.Evaluate("просто текст", Scope());

        Assert.Equal("просто текст", (string?)result);
    }

    [Fact]
    public void Evaluate_RegisteredExpression_ReturnsCopyOfAnswer()
    {
        var answer = new JsonArray(1, 2);
        var evaluator = new FakeExpressionEvaluator().Set("{{ $json.lines }}", answer);

        var first = evaluator.Evaluate("{{ $json.lines }}", Scope());
        Assert.NotNull(first);
        ((JsonArray)first).Add(3);
        var second = evaluator.Evaluate("{{ $json.lines }}", Scope());

        Assert.Equal(2, ((JsonArray)second!).Count);
        Assert.Equal(2, evaluator.Calls.Count);
    }

    [Fact]
    public void Evaluate_RegisteredNull_ReturnsNull()
    {
        var evaluator = new FakeExpressionEvaluator().Set("{{ null }}", null);

        Assert.Null(evaluator.Evaluate("{{ null }}", Scope()));
    }

    [Fact]
    public void Evaluate_UnknownExpression_Throws()
    {
        var evaluator = new FakeExpressionEvaluator();

        var ex = Assert.Throws<ExpressionException>(() => evaluator.Evaluate("{{ $json.a }}", Scope()));

        Assert.Equal("{{ $json.a }}", ex.Expression);
    }

    [Fact]
    public void Evaluate_UnknownExpression_UsesFallback()
    {
        var evaluator = new FakeExpressionEvaluator
        {
            Fallback = (expr, scope) => scope.Json["a"]?.DeepClone(),
        };

        var result = evaluator.Evaluate("{{ $json.a }}", Scope());

        Assert.Equal(1, (int?)result);
    }

    [Fact]
    public void Scope_DefaultsAreSafe()
    {
        var scope = Scope();

        Assert.Empty(scope.Input);
        Assert.Empty(scope.Nodes);
        Assert.Empty(scope.Env);
        Assert.Equal("UTC", scope.Timezone);
        Assert.Same(scope.Item.Json, scope.Json);
    }
}
