using System.Text.Json.Nodes;
using OpenBP.Engine.Execution;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Testing;
using OpenBP.Engine.Validation;
using OpenBP.Expressions;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core.Tests;

/// <summary>F6-19: таблиця рішень <c>core.decisionTable</c>.</summary>
public class DecisionTableNodeTests
{
    private readonly DecisionTableNode _node = new();

    /// <summary>Знижка за сегментом і сумою; параметри — як після обчислення входів движком.</summary>
    private static string Table(string segment, double total, string hitPolicy) => $$"""
        {
          "hitPolicy": "{{hitPolicy}}",
          "inputs": [{ "name": "segment", "expression": "{{segment}}" }, { "name": "total", "expression": {{total}} }],
          "outputs": [{ "name": "discount" }, { "name": "note" }],
          "rules": [
            { "when": [{ "operator": "equals", "right": "vip" }, { "operator": "gte", "right": 1000 }], "then": [15, "VIP великий"] },
            { "when": [{ "operator": "equals", "right": "vip" }, "-"], "then": [10, "VIP"] },
            { "when": ["-", { "operator": "gte", "right": 5000 }], "then": [5, "великий"] }
          ]
        }
        """;

    /// <summary>Очікувані items у тій самій серіалізації, що й <see cref="Ctx.Json"/> (кирилиця екранується).</summary>
    private static string[] Expected(params string[] items) => [.. items.Select(i => JsonNode.Parse(i)!.ToJsonString())];

    private FakeNodeContext For(string hitPolicy, params (string Segment, double Total)[] rows)
    {
        var ctx = Ctx.For(_node, "{}", [.. rows.Select(r => $$"""{"segment":"{{r.Segment}}","total":{{r.Total}}}""")]);
        ctx.ParametersForItem = i => (JsonObject)JsonNode.Parse(Table(rows[i].Segment, rows[i].Total, hitPolicy))!;
        return ctx;
    }

    [Fact]
    public async Task First_takes_the_first_matching_rule_and_dash_matches_anything()
    {
        var output = await _node.ExecuteAsync(For("first", ("vip", 2000), ("vip", 10), ("retail", 7000)), CancellationToken.None);

        Assert.Equal(2, output.Length);
        Assert.Equal(
            Expected(
                """{"segment":"vip","total":2000,"discount":15,"note":"VIP великий"}""",
                """{"segment":"vip","total":10,"discount":10,"note":"VIP"}""",
                """{"segment":"retail","total":7000,"discount":5,"note":"великий"}"""),
            Ctx.Json(output[0]));
        Assert.Empty(output[1]);
    }

    [Fact]
    public async Task All_emits_an_item_per_matching_rule()
    {
        var output = await _node.ExecuteAsync(For("all", ("vip", 6000)), CancellationToken.None);

        Assert.Equal([15, 10, 5], output[0].Select(i => i.Json["discount"]!.GetValue<int>()));
        Assert.All(output[0], i => Assert.Equal("vip", i.Json["segment"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Collect_puts_arrays_of_all_matches_into_one_item()
    {
        var output = await _node.ExecuteAsync(For("collect", ("vip", 6000), ("retail", 9000)), CancellationToken.None);

        Assert.Equal(
            Expected(
                """{"segment":"vip","total":6000,"discount":[15,10,5],"note":["VIP великий","VIP","великий"]}""",
                """{"segment":"retail","total":9000,"discount":[5],"note":["великий"]}"""),
            Ctx.Json(output[0]));
    }

    [Fact]
    public async Task No_match_goes_to_noMatch_unchanged()
    {
        var output = await _node.ExecuteAsync(For("first", ("retail", 100), ("vip", 1)), CancellationToken.None);

        Assert.Equal(Expected("""{"segment":"vip","total":1,"discount":10,"note":"VIP"}"""), Ctx.Json(output[0]));
        Assert.Equal(["""{"segment":"retail","total":100}"""], Ctx.Json(output[1]));
    }

    [Fact]
    public async Task Wrong_cell_count_and_bad_operator_name_the_rule()
    {
        var ctx = Ctx.For(_node, """
            { "inputs": [{ "name": "a", "expression": 1 }], "outputs": [{ "name": "x" }],
              "rules": [{ "when": ["-"], "then": [1] }, { "when": ["-", "-"], "then": [2] }] }
            """, "{}");
        var count = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.StartsWith("Правило 2: умов 2, а входів 1", count.Message, StringComparison.Ordinal);

        ctx = Ctx.For(_node, """
            { "inputs": [{ "name": "сума", "expression": "abc" }], "outputs": [{ "name": "x" }],
              "rules": [{ "when": [{ "operator": "gt", "right": 5 }], "then": [1] }] }
            """, "{}");
        var type = await Assert.ThrowsAsync<CoreNodeException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));
        Assert.StartsWith("Правило 1, вхід «сума»: Оператор «gt» порівнює лише числа", type.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Expression_error_in_a_rule_names_the_rule_number()
    {
        var ctx = Ctx.For(_node, "{}", "{}");
        ctx.ParametersForItem = _ => throw new ExpressionException("{{ $json.a.b }}", "Вираз у параметрі «rules[2].then[0]» не обчислився: a is undefined.") { Path = "rules[2].then[0]" };

        var error = await Assert.ThrowsAsync<ExpressionException>(() => _node.ExecuteAsync(ctx, CancellationToken.None));

        Assert.StartsWith("Правило 3: Вираз у параметрі «rules[2].then[0]»", error.Message, StringComparison.Ordinal);
        Assert.Equal("rules[2].then[0]", error.Path);
    }

    [Fact]
    public async Task Runs_through_the_engine_with_expressions_and_reports_rule_of_failed_expression()
    {
        var registry = new InMemoryNodeRegistry();
        foreach (var node in CoreNodes.All())
        {
            registry.Register(node);
        }

        var table = """
            { "inputs": [{ "name": "segment", "expression": "{{ $json.segment }}" }], "outputs": [{ "name": "discount" }],
              "rules": [
                { "when": [{ "operator": "equals", "right": "vip" }], "then": ["{{ $json.base * 2 }}"] },
                { "when": ["-"], "then": ["{{ $json.base }}"] }
              ] }
            """;
        var ok = await RunAsync(registry, """{"fields":[{"name":"segment","value":"vip"},{"name":"base","value":5}]}""", table);
        Assert.Equal(ExecutionStatus.Success, ok.Status);
        Assert.Equal(10, ok.OutputOf("d").Single().Json["discount"]!.GetValue<int>());

        // Вирази обчислюються для всієї таблиці, тож помилка в правилі 2 видна навіть без його збігу — з номером правила.
        var failed = await RunAsync(registry, """{"fields":[{"name":"segment","value":"vip"},{"name":"base","value":5}]}""",
            table.Replace("\"then\": [\"{{ $json.base }}\"]", "\"then\": [\"{{ $json.missing.deep }}\"]", StringComparison.Ordinal));
        Assert.Equal(ExecutionStatus.Error, failed.Status);
        Assert.StartsWith("Правило 2:", failed.Error!.Message, StringComparison.Ordinal);

        var validator = new WorkflowValidator(new NodeRegistrySchemaProvider(registry));
        var issues = await validator.ValidateAsync(WorkflowJson.Serialize(Workflow("""{"fields":[]}""", table)));
        Assert.True(issues.Count == 0, string.Join("; ", issues.Select(i => i.Message)));
    }

    private static async Task<ExecutionResult> RunAsync(InMemoryNodeRegistry registry, string set, string table)
    {
        var workflow = Workflow(set, table);
        var store = new InMemoryExecutionStore();
        var request = new ExecutionRequest { ExecutionId = Guid.NewGuid(), Workflow = workflow };
        store.Register(new ExecutionSnapshot
        {
            Id = request.ExecutionId,
            WorkflowId = "wf_dt",
            Workflow = workflow,
            Mode = request.Mode,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var executor = new WorkflowExecutor(
            registry, store, new JintExpressionEvaluator(), new FakeCredentialProvider(), new InMemoryBinaryStore(), new InMemoryExecutionLogger());
        return await executor.RunAsync(request, CancellationToken.None);
    }

    private static WorkflowDefinition Workflow(string set, string table)
    {
        var workflow = new WorkflowDefinition { Name = "Таблиця рішень", Settings = new() { Timezone = "Europe/Kyiv" } };
        Add(workflow, "t", "core.manualTrigger", "{}");
        Add(workflow, "s", "core.set", set);
        Add(workflow, "d", "core.decisionTable", table);
        workflow.Connections.Add(new Connection { From = new ConnectionEndpoint { Node = "t", Output = 0 }, To = new ConnectionEndpoint { Node = "s", Input = 0 } });
        workflow.Connections.Add(new Connection { From = new ConnectionEndpoint { Node = "s", Output = 0 }, To = new ConnectionEndpoint { Node = "d", Input = 0 } });
        return workflow;
    }

    private static void Add(WorkflowDefinition workflow, string id, string type, string parameters) =>
        workflow.Nodes.Add(new NodeDefinition
        {
            Id = id,
            Name = $"Вузол {id}",
            Type = type,
            Parameters = (JsonObject)JsonNode.Parse(parameters)!,
        });
}
