using System.Text.Json.Nodes;

namespace OpenBP.Cli.Tests;

/// <summary>F3-105: еталонні описи (<c>tests/drafter-cases</c>) і порівняння сценарію з очікуваннями.</summary>
public class DraftEvalTests
{
    private static readonly string[] ExpectKeys = ["trigger", "requireTypes", "minNodes", "maxNodes", "minBranches", "minLanes"];

    private static string CasesDir => Path.Combine(AppContext.BaseDirectory, "drafter-cases");

    private static readonly JsonNode Approval = JsonNode.Parse("""
        {
          "business": { "lanes": [ { "id": "lane_1", "name": "Працівник" }, { "id": "lane_2", "name": "Керівник" } ] },
          "nodes": [
            { "id": "a", "type": "core.formTrigger" },
            { "id": "b", "type": "core.approval" },
            { "id": "c", "type": "core.set" },
            { "id": "d", "type": "core.set" }
          ],
          "connections": [
            { "from": { "node": "a", "output": 0 }, "to": { "node": "b", "input": 0 } },
            { "from": { "node": "b", "output": 1 }, "to": { "node": "c", "input": 0 } },
            { "from": { "node": "b", "output": 2 }, "to": { "node": "d", "input": 0 } }
          ]
        }
        """)!;

    [Fact]
    public void Reference_set_has_fifteen_unique_well_formed_cases()
    {
        var cases = DraftEval.LoadCases(CasesDir, only: null);

        Assert.Equal(15, cases.Count);
        Assert.Equal(cases.Count, cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, c =>
        {
            Assert.InRange(c.Description.Length, 20, 4000);
            var expect = Assert.IsType<JsonObject>(c.Expect);
            Assert.All(expect.Select(p => p.Key), key => Assert.Contains(key, ExpectKeys));
            Assert.NotEmpty(Assert.IsType<JsonArray>(expect["trigger"]));
        });
    }

    [Fact]
    public void Only_filters_cases_by_id_and_unknown_filter_is_an_error()
    {
        Assert.Single(DraftEval.LoadCases(CasesDir, "vacation"));
        Assert.Throws<CliException>(() => DraftEval.LoadCases(CasesDir, "немає-такого"));
        Assert.Throws<CliException>(() => DraftEval.LoadCases(Path.Combine(CasesDir, "nope"), null));
    }

    [Fact]
    public void Matching_definition_has_no_problems()
    {
        var expect = JsonNode.Parse("""
            { "trigger": ["core.manualTrigger", "core.formTrigger"], "requireTypes": [["core.approval"], ["mcp.tool", "core.set"]],
              "minNodes": 3, "maxNodes": 5, "minBranches": 1, "minLanes": 2 }
            """);

        Assert.Empty(DraftEval.Check(Approval, expect));
        Assert.Empty(DraftEval.Check(Approval, null));
    }

    [Fact]
    public void Every_mismatch_is_reported()
    {
        var expect = JsonNode.Parse("""
            { "trigger": ["core.schedule"], "requireTypes": [["core.loop", "core.wait"]],
              "minNodes": 6, "maxNodes": 3, "minBranches": 2, "minLanes": 3 }
            """);

        var problems = DraftEval.Check(Approval, expect);

        Assert.Equal(6, problems.Count);
        Assert.Contains(problems, p => p.Contains("core.formTrigger", StringComparison.Ordinal) && p.Contains("core.schedule", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("core.loop/core.wait", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("мінімум 6", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("максимум 3", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("розгалужень 1", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("доріжок 2", StringComparison.Ordinal));
    }
}
