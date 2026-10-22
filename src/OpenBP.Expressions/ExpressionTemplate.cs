using OpenBP.Engine.Expressions;

namespace OpenBP.Expressions;

/// <summary>Розбір рядка параметра на літерали і вирази <c>{{ … }}</c> за правилами <see cref="ExpressionSyntax"/>.</summary>
internal static class ExpressionTemplate
{
    /// <summary>Частина рядка: літерал або тіло виразу (без маркерів).</summary>
    internal readonly record struct Segment(string Text, bool IsExpression);

    /// <summary>
    /// Розбиває рядок: кожен <c>{{</c> закривається найближчим <c>}}</c>; незакритий <c>{{</c> лишається літералом.
    /// </summary>
    public static List<Segment> Parse(string value)
    {
        var segments = new List<Segment>();
        var position = 0;
        while (position < value.Length)
        {
            var open = value.IndexOf(ExpressionSyntax.Open, position, StringComparison.Ordinal);
            var close = open < 0
                ? -1
                : value.IndexOf(ExpressionSyntax.Close, open + ExpressionSyntax.Open.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                segments.Add(new Segment(value[position..], IsExpression: false));
                break;
            }

            if (open > position)
            {
                segments.Add(new Segment(value[position..open], IsExpression: false));
            }

            segments.Add(new Segment(value[(open + ExpressionSyntax.Open.Length)..close], IsExpression: true));
            position = close + ExpressionSyntax.Close.Length;
        }

        return segments;
    }

    /// <summary>Тіло єдиного виразу у рядку, для якого <see cref="ExpressionSyntax.IsSingleExpression"/> істинне.</summary>
    public static string SingleBody(string value)
    {
        var trimmed = value.Trim();
        return trimmed[ExpressionSyntax.Open.Length..^ExpressionSyntax.Close.Length];
    }
}
