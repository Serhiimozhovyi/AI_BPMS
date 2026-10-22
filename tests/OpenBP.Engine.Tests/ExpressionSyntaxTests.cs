using OpenBP.Engine.Expressions;

namespace OpenBP.Engine.Tests;

public class ExpressionSyntaxTests
{
    [Theory]
    [InlineData("{{ $json.a }}", true)]
    [InlineData("Замовлення №{{ $json.number }} створено", true)]
    [InlineData("{{a}}", true)]
    [InlineData("просто текст", false)]
    [InlineData("{{ незакритий", false)]
    [InlineData("}} {{", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsExpression_DetectsMarkers(string? value, bool expected)
    {
        Assert.Equal(expected, ExpressionSyntax.ContainsExpression(value));
    }

    [Theory]
    [InlineData("{{ $json.lines }}", true)]
    [InlineData("  {{ $json.lines }}  ", true)]
    [InlineData("{{a}}", true)]
    [InlineData("№{{ $json.number }}", false)]
    [InlineData("{{ a }} і {{ b }}", false)]
    [InlineData("{{}}", true)]
    [InlineData("{{", false)]
    [InlineData("текст", false)]
    [InlineData(null, false)]
    public void IsSingleExpression_RequiresWholeStringToBeOneExpression(string? value, bool expected)
    {
        Assert.Equal(expected, ExpressionSyntax.IsSingleExpression(value));
    }
}
