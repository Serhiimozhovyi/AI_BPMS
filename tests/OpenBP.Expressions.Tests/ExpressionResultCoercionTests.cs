using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;

namespace OpenBP.Expressions.Tests;

public class ExpressionResultCoercionTests
{
    private const string Expr = "{{ $json.x }}";

    private static JsonNode? Coerce(string valueJson, string schemaJson) =>
        ExpressionResultCoercion.Coerce(Expr, JsonNode.Parse(valueJson), JsonNode.Parse(schemaJson));

    [Theory]
    [InlineData("\"5\"", """{"type":"integer"}""", "5")]
    [InlineData("\" 12.5 \"", """{"type":"number"}""", "12.5")]
    [InlineData("\"true\"", """{"type":"boolean"}""", "true")]
    [InlineData("42", """{"type":"string"}""", "\"42\"")]
    [InlineData("false", """{"type":"string"}""", "\"false\"")]
    [InlineData("""{"a":1}""", """{"type":"string"}""", "\"{\\\"a\\\":1}\"")]
    [InlineData("\"[1,2]\"", """{"type":"array"}""", "[1,2]")]
    [InlineData("\"{\\\"a\\\":1}\"", """{"type":"object"}""", """{"a":1}""")]
    [InlineData("\"7\"", """{"type":["integer","string"]}""", "\"7\"")]
    [InlineData("\"7\"", """{"type":["integer","null"]}""", "7")]
    [InlineData("3", """{"type":"number"}""", "3")]
    [InlineData("\"x\"", """{"description":"без типу"}""", "\"x\"")]
    public void Coerce_ConvertsToSchemaType(string valueJson, string schemaJson, string expectedJson)
    {
        var result = Coerce(valueJson, schemaJson);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), result), result?.ToJsonString());
    }

    [Fact]
    public void Coerce_NullStaysNull()
    {
        Assert.Null(ExpressionResultCoercion.Coerce(Expr, null, JsonNode.Parse("""{"type":"integer"}""")));
    }

    [Fact]
    public void Coerce_WithoutSchema_ReturnsValue()
    {
        var value = JsonValue.Create(1);

        Assert.Same(value, ExpressionResultCoercion.Coerce(Expr, value, null));
    }

    [Theory]
    [InlineData("\"abc\"", """{"type":"integer"}""")]
    [InlineData("12.5", """{"type":"integer"}""")]
    [InlineData("\"так\"", """{"type":"boolean"}""")]
    [InlineData("\"[1,2]\"", """{"type":"object"}""")]
    [InlineData("\"{oops\"", """{"type":"array"}""")]
    [InlineData("true", """{"type":"number"}""")]
    public void Coerce_Impossible_Throws(string valueJson, string schemaJson)
    {
        var ex = Assert.Throws<ExpressionException>(() => Coerce(valueJson, schemaJson));

        Assert.Equal(Expr, ex.Expression);
    }
}
