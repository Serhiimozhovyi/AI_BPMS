using System.Text.Json.Nodes;
using OpenBP.Nodes.Core.Conditions;

namespace OpenBP.Nodes.Core.Tests;

public class ConditionEvaluatorTests
{
    [Theory]
    [InlineData("1", "equals", "1.0", true)]
    [InlineData("\"1\"", "equals", "1", false)]
    [InlineData("{\"a\":[1]}", "equals", "{\"a\":[1]}", true)]
    [InlineData("1", "notEquals", "2", true)]
    [InlineData("\"10\"", "gt", "9", true)]
    [InlineData("3", "gte", "3", true)]
    [InlineData("2", "lt", "3", true)]
    [InlineData("3", "lte", "2", false)]
    [InlineData("\"заявка 42\"", "contains", "\"42\"", true)]
    [InlineData("[1,2,3]", "contains", "2", true)]
    [InlineData("[1,2,3]", "notContains", "4", true)]
    [InlineData("\"invoice.pdf\"", "endsWith", "\".pdf\"", true)]
    [InlineData("\"\"", "isEmpty", "null", true)]
    [InlineData("null", "isEmpty", "null", true)]
    [InlineData("[]", "isNotEmpty", "null", false)]
    [InlineData("{\"a\":1}", "isNotEmpty", "null", true)]
    [InlineData("true", "isTrue", "null", true)]
    [InlineData("false", "isFalse", "null", true)]
    public void Operators(string left, string op, string right, bool expected)
    {
        var condition = new Condition { Left = JsonNode.Parse(left), Operator = op, Right = JsonNode.Parse(right) };

        Assert.Equal(expected, ConditionEvaluator.Evaluate(condition));
    }

    [Theory]
    [InlineData("\"true\"", "isTrue", "null")]
    [InlineData("5", "contains", "5")]
    [InlineData("5", "startsWith", "\"5\"")]
    [InlineData("{}", "lt", "1")]
    public void Incompatible_types_fail(string left, string op, string right)
    {
        var condition = new Condition { Left = JsonNode.Parse(left), Operator = op, Right = JsonNode.Parse(right) };

        Assert.Throws<CoreNodeException>(() => ConditionEvaluator.Evaluate(condition));
    }

    [Fact]
    public void Unknown_combine_fails() =>
        Assert.Throws<CoreNodeException>(() => ConditionEvaluator.Evaluate(new ConditionGroup { Combine = "xor" }));
}
