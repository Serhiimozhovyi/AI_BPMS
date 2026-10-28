using System.Text.Json.Nodes;
using OpenBP.Application.Assistant;
using OpenBP.Application.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Tests;

/// <summary>F3-103: план з опису → сценарій кодом — порти гілок, доріжки, розкладка, дії конекторів, заглушки.</summary>
public class DraftCompilerTests
{
    private static readonly Guid Server = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static NodeDescriptor Node(string type, string[] inputs, string[] outputs, IReadOnlyList<CredentialRequirement>? credentials = null) =>
        new(type, 1, type, NodeGroup.Logic, inputs, outputs, new JsonObject { ["type"] = "object" }, credentials);

    private static readonly Dictionary<string, NodeDescriptor> Descriptors = new[]
    {
        Node("core.manualTrigger", [], ["main"]),
        Node("core.formTrigger", [], ["main"]),
        Node("core.if", ["main"], ["true", "false"]),
        Node("core.approval", ["main"], ["timeout", "decision0", "decision1"]),
        Node("core.set", ["main"], ["main"]),
        Node("core.merge", ["in0", "in1"], ["main"]),
        Node("ai.llm", ["main"], ["main"], [new CredentialRequirement("llm", "anthropic")]),
        Node("mcp.tool", ["main"], ["main"]),
    }.ToDictionary(d => d.Type);

    private static readonly CatalogDigest Digest = new(
        [.. Descriptors.Values.Select(d => new DigestNode(d.Type, d.Version, d.Type, "", d.IsTrigger ? "trigger" : "action", d.Outputs))],
        [new DigestAction($"mcp:{Server}/send_message", "Telegram", "Надіслати", "")]);

    private static readonly WorkflowDefinition Baseline = new() { Name = "Відпустка", Settings = new WorkflowSettings { Timezone = "Europe/Kyiv" } };

    private static DraftStep Step(string id, string kind, string use, string? lane = null) => new(id, kind, $"Крок {id}", lane, use, null);

    private static readonly DraftPlan Vacation = new(
        "Відпустка",
        ["Працівник", "Керівник"],
        [
            Step("s1", "trigger", "core.formTrigger", "Працівник"),
            Step("s2", "condition", "core.if", "Керівник"),
            Step("s3", "approval", "core.approval", "Керівник"),
            Step("s4", "notify", $"mcp:{Server}/send_message", "Кадри"),
            Step("s5", "notify", "none", "Працівник"),
            Step("s6", "transform", "ai.llm"),
        ],
        [
            new("s1", "s2", null),
            new("s2", "s3", "так"),
            new("s2", "s6", "false"),
            new("s3", "s4", "approved"),
            new("s3", "s5", "decision1"),
        ]);

    [Fact]
    public void Plan_compiles_to_nodes_with_branch_ports_lanes_and_layout()
    {
        Assert.Empty(DraftCompiler.Check(Vacation, Digest, Descriptors));

        var result = DraftCompiler.Compile(Vacation, Digest, Descriptors, Baseline);
        var definition = result.Definition;

        Assert.Equal("Відпустка", definition.Name);
        Assert.Equal("Europe/Kyiv", definition.Settings.Timezone);
        Assert.Equal(["n_s1", "n_s2", "n_s3", "n_s4", "n_s5", "n_s6"], definition.Nodes.Select(n => n.Id));
        Assert.Equal(["core.formTrigger", "core.if", "core.approval", "mcp.tool", "core.set", "ai.llm"], definition.Nodes.Select(n => n.Type));

        int Output(string from, string to) => definition.Connections.Single(c => c.From.Node == from && c.To.Node == to).From.Output!.Value;
        Assert.Equal(0, Output("n_s1", "n_s2"));
        Assert.Equal(0, Output("n_s2", "n_s3"));
        Assert.Equal(1, Output("n_s2", "n_s6"));
        Assert.Equal(1, Output("n_s3", "n_s4"));
        Assert.Equal(2, Output("n_s3", "n_s5"));

        // Доріжки: з плану і невідома «Кадри» з кроку; крок без доріжки — без business.
        Assert.Equal(["Працівник", "Керівник", "Кадри"], definition.Business!.Lanes!.Select(l => l.Name));
        var lanes = definition.Business.Lanes!.ToDictionary(l => l.Name, l => l.Id);
        Assert.Equal(lanes["Керівник"], definition.Nodes.Single(n => n.Id == "n_s3").Business!.Lane);
        Assert.Equal(lanes["Кадри"], definition.Nodes.Single(n => n.Id == "n_s4").Business!.Lane);
        Assert.Null(definition.Nodes.Single(n => n.Id == "n_s6").Business);

        // Зліва направо по рангу, у ранзі — згори вниз.
        double[] Position(string id) => definition.Nodes.Single(n => n.Id == id).Position;
        Assert.Equal([0, 0], Position("n_s1"));
        Assert.Equal([240, 0], Position("n_s2"));
        Assert.Equal([480, 0], Position("n_s3"));
        Assert.Equal([480, 120], Position("n_s6"));
        Assert.Equal([720, 0], Position("n_s4"));
        Assert.Equal([720, 120], Position("n_s5"));

        // Дія конектора — mcp.tool з serverId/toolName; крок без вузла — заглушка і todo; облікові дані — todo.
        var tool = definition.Nodes.Single(n => n.Id == "n_s4");
        Assert.Equal(Server.ToString(), (string?)tool.Parameters["serverId"]);
        Assert.Equal("send_message", (string?)tool.Parameters["toolName"]);
        Assert.NotNull(definition.Nodes.Single(n => n.Id == "n_s5").Notes);
        Assert.Contains(result.Todo, t => t.NodeId == "n_s5");
        Assert.Contains(result.Todo, t => t.NodeId == "n_s6" && t.Text.Contains("облікові дані", StringComparison.Ordinal));
    }

    [Fact]
    public void Merge_inputs_are_assigned_in_order_and_names_ids_are_unique()
    {
        var plan = new DraftPlan(
            "",
            [],
            [
                Step("start", "trigger", "core.manualTrigger"),
                new DraftStep("a-1", "action", "Дія", null, "core.set", null),
                new DraftStep("a 1", "action", "Дія", null, "core.set", null),
                Step("join", "transform", "core.merge"),
            ],
            [new("start", "a-1", null), new("start", "a 1", null), new("a-1", "join", null), new("a 1", "join", null)]);

        Assert.Empty(DraftCompiler.Check(plan, Digest, Descriptors));
        var definition = DraftCompiler.Compile(plan, Digest, Descriptors, Baseline).Definition;

        Assert.Equal(["n_start", "n_a_1", "n_a_1_2", "n_join"], definition.Nodes.Select(n => n.Id));
        Assert.Equal(["Крок start", "Дія", "Дія 2", "Крок join"], definition.Nodes.Select(n => n.Name));
        Assert.Equal([0, 1], definition.Connections.Where(c => c.To.Node == "n_join").Select(c => c.To.Input!.Value));
        Assert.Null(definition.Business);
    }

    [Fact]
    public void Check_reports_unknown_use_missing_trigger_bad_branch_and_unreachable_steps()
    {
        var plan = new DraftPlan(
            "",
            [],
            [Step("s1", "action", "core.set"), Step("s2", "condition", "core.if"), Step("s3", "action", "core.nope")],
            [new("s1", "s2", null), new("s2", "s3", "maybe"), new("s2", "ghost", null)]);

        var errors = DraftCompiler.Check(plan, Digest, Descriptors);

        Assert.Contains(errors, e => e.Contains("core.nope", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Немає тригера", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("maybe", StringComparison.Ordinal) && e.Contains("true, false", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("ghost", StringComparison.Ordinal));

        // Остання спроба: невідомий use не є помилкою — крок стане заглушкою.
        Assert.DoesNotContain(DraftCompiler.Check(plan, Digest, Descriptors, lenient: true), e => e.Contains("core.nope", StringComparison.Ordinal));

        var twoTriggers = new DraftPlan("", [], [Step("a", "trigger", "core.manualTrigger"), Step("b", "trigger", "core.formTrigger"), Step("c", "action", "core.set")], [new("a", "c", null)]);
        var twoErrors = DraftCompiler.Check(twoTriggers, Digest, Descriptors);
        Assert.Contains(twoErrors, e => e.Contains("Тригерів 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("decision0", 1)]
    [InlineData("Rejected", 2)]
    [InlineData("timeout", 0)]
    [InlineData("2", 2)]
    [InlineData("whatever", null)]
    public void Approval_branches_resolve_to_ports(string? branch, int? expected) =>
        Assert.Equal(expected, DraftCompiler.ResolveBranch(branch, ["timeout", "decision0", "decision1"]));
}
