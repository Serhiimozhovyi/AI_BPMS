using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Nodes;
using OpenBP.Nodes.Core.Conditions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.decisionTable</c> — таблиця рішень (F6-19): для кожного item'а обчислює входи, перевіряє правила (по умові
/// на кожен вхід, оператори — як у <c>core.if</c>, <c>"-"</c> — будь-яке значення) і записує значення виходів правила
/// в поля item'а. <c>hitPolicy</c>: <c>first</c> — перше правило, що збіглося; <c>all</c> — окремий item на кожне;
/// <c>collect</c> — один item, у полях виходів — масиви значень усіх збігів. Жодного збігу — item іде на вихід
/// <c>noMatch</c> без змін.
/// </summary>
public sealed partial class DecisionTableNode : INode
{
    /// <summary>Комірка «будь-яке значення».</summary>
    public const string Any = "-";

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.decisionTable",
        Version: 1,
        DisplayName: "Таблиця рішень",
        Group: NodeGroup.Logic,
        Inputs: ["main"],
        Outputs: ["main", "noMatch"],
        ParametersSchema: NodeSchemas.Load("decisionTable"),
        Description: "Підбирає значення полів за таблицею правил: рядки — правила, колонки — умови на входи й значення виходів; без збігу — вихід noMatch.");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        var matched = new List<Item>();
        var noMatch = new List<Item>();
        for (var i = 0; i < items.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = Parameters(ctx, i);
            try
            {
                var hits = Hits(p);
                if (hits.Count == 0)
                {
                    noMatch.Add(ItemCopy.Clone(items[i]));
                    continue;
                }

                switch (p.HitPolicy)
                {
                    case "first":
                        matched.Add(WithOutputs(items[i], p.Outputs, hits[0].Then));
                        break;
                    case "all":
                        matched.AddRange(hits.Select(rule => WithOutputs(items[i], p.Outputs, rule.Then)));
                        break;
                    default:
                        matched.Add(Collected(items[i], p.Outputs, hits));
                        break;
                }
            }
            catch (CoreNodeException ex)
            {
                throw ex.AtItem(i, items.Count);
            }
        }

        return Task.FromResult<Item[][]>([[.. matched], [.. noMatch]]);
    }

    /// <summary>Параметри item'а; помилка виразу в правилі отримує номер правила.</summary>
    private static DecisionTableParameters Parameters(INodeContext ctx, int itemIndex)
    {
        DecisionTableParameters p;
        try
        {
            p = ctx.Params<DecisionTableParameters>(itemIndex);
        }
        catch (ExpressionException ex) when (ex.Path is { } path && RulePath().Match(path) is { Success: true } match)
        {
            var rule = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
            throw new ExpressionException(ex.Expression, $"Правило {rule}: {ex.Message}", ex)
            {
                Details = ex.Details,
                ItemIndex = ex.ItemIndex,
                Path = path,
            };
        }

        if (p.HitPolicy is not ("first" or "all" or "collect"))
        {
            throw new CoreNodeException($"Невідома політика «{p.HitPolicy}». Оберіть first, all або collect.");
        }

        for (var r = 0; r < p.Rules.Count; r++)
        {
            var rule = p.Rules[r];
            if (rule.When.Count != p.Inputs.Count)
            {
                throw new CoreNodeException($"Правило {r + 1}: умов {rule.When.Count}, а входів {p.Inputs.Count}. Задайте умову (або «-») для кожного входу.");
            }

            if (rule.Then.Count != p.Outputs.Count)
            {
                throw new CoreNodeException($"Правило {r + 1}: значень {rule.Then.Count}, а виходів {p.Outputs.Count}. Задайте значення для кожного виходу.");
            }
        }

        return p;
    }

    private static List<DecisionRule> Hits(DecisionTableParameters p)
    {
        var hits = new List<DecisionRule>();
        for (var r = 0; r < p.Rules.Count; r++)
        {
            if (Matches(p, r))
            {
                hits.Add(p.Rules[r]);
                if (p.HitPolicy == "first")
                {
                    break;
                }
            }
        }

        return hits;
    }

    private static bool Matches(DecisionTableParameters p, int ruleIndex)
    {
        var rule = p.Rules[ruleIndex];
        for (var c = 0; c < p.Inputs.Count; c++)
        {
            var input = p.Inputs[c];
            try
            {
                if (Condition(rule.When[c], input.Expression) is { } condition && !ConditionEvaluator.Evaluate(condition))
                {
                    return false;
                }
            }
            catch (CoreNodeException ex)
            {
                throw new CoreNodeException($"Правило {ruleIndex + 1}, вхід «{input.Name}»: {ex.Message}", ex);
            }
        }

        return true;
    }

    /// <summary>Умова комірки над значенням входу; <c>null</c> — «будь-яке».</summary>
    private static Condition? Condition(JsonNode? cell, JsonNode? value) => cell switch
    {
        null => null,
        JsonValue text when text.GetValueKind() == JsonValueKind.String && text.GetValue<string>() == Any => null,
        JsonObject obj when obj["operator"] is JsonValue op && op.GetValueKind() == JsonValueKind.String => new Condition
        {
            Left = value?.DeepClone(),
            Operator = op.GetValue<string>(),
            Right = obj["right"]?.DeepClone(),
        },
        _ => throw new CoreNodeException($"Комірка має бути «{Any}» або умовою {{ operator, right }}, а отримано {cell.ToJsonString()}."),
    };

    private static Item WithOutputs(Item item, List<DecisionOutput> outputs, List<JsonNode?> values)
    {
        var copy = ItemCopy.Clone(item);
        for (var o = 0; o < outputs.Count; o++)
        {
            copy.Json[outputs[o].Name] = values[o]?.DeepClone();
        }

        return copy;
    }

    private static Item Collected(Item item, List<DecisionOutput> outputs, List<DecisionRule> hits)
    {
        var copy = ItemCopy.Clone(item);
        for (var o = 0; o < outputs.Count; o++)
        {
            copy.Json[outputs[o].Name] = new JsonArray([.. hits.Select(rule => rule.Then[o]?.DeepClone())]);
        }

        return copy;
    }

    [GeneratedRegex(@"^rules\[(\d+)\]")]
    private static partial Regex RulePath();

    private sealed class DecisionTableParameters
    {
        public List<DecisionInput> Inputs { get; set; } = [];

        public List<DecisionOutput> Outputs { get; set; } = [];

        public List<DecisionRule> Rules { get; set; } = [];

        public string HitPolicy { get; set; } = "first";
    }

    private sealed class DecisionInput
    {
        public string Name { get; set; } = "";

        public JsonNode? Expression { get; set; }
    }

    private sealed class DecisionOutput
    {
        public string Name { get; set; } = "";
    }

    private sealed class DecisionRule
    {
        public List<JsonNode?> When { get; set; } = [];

        public List<JsonNode?> Then { get; set; } = [];
    }
}
