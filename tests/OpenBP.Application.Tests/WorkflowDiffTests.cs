using System.Text.Json.Nodes;
using OpenBP.Application.Workflows;

namespace OpenBP.Application.Tests;

/// <summary>F3-116: відмінності двох визначень для плану зміни сценарію.</summary>
public class WorkflowDiffTests
{
    private static readonly WorkflowPlan Empty = new("p", Guid.Empty, "Сценарій", 1);

    private static JsonObject Document(string nodes, string connections = "[]", string settings = """{ "timezone": "UTC" }""") =>
        JsonNode.Parse($$"""{ "formatVersion": 1, "name": "x", "settings": {{settings}}, "nodes": {{nodes}}, "connections": {{connections}} }""")!.AsObject();

    private const string Start = """{ "id": "a", "name": "Старт", "type": "core.manualTrigger", "position": [0, 0], "parameters": {} }""";

    [Fact]
    public void Same_definition_has_no_changes()
    {
        var plan = WorkflowDiff.Fill(Empty, Document($"[{Start}]"), Document($"[{Start}]"));
        Assert.False(plan.HasChanges);
        Assert.True(plan.Valid);
    }

    [Fact]
    public void Parameters_are_compared_by_key_and_renames_and_type_changes_are_named()
    {
        var before = Document("""[ { "id": "b", "name": "Лист", "type": "core.set", "parameters": { "to": "a@x", "subject": "Привіт", "old": 1 } } ]""");
        var after = Document("""[ { "id": "b", "name": "Повідомлення", "type": "core.code", "parameters": { "to": "b@x", "subject": "Привіт", "body": "…" } } ]""");

        var change = Assert.Single(WorkflowDiff.Fill(Empty, before, after).ChangedNodes);
        Assert.Equal(["name", "type", "parameters.to", "parameters.body", "parameters.old"], change.Fields);
        Assert.Equal("Лист", change.RenamedFrom);
        Assert.Equal("core.set", change.TypeChangedFrom);
        Assert.Equal(("Повідомлення", "core.code"), (change.Name, change.Type));
    }

    [Fact]
    public void Connections_default_ports_to_zero_and_carry_node_names()
    {
        const string nodes = $$"""[ {{Start}}, { "id": "b", "name": "Б", "type": "core.set", "parameters": {} } ]""";
        var before = Document(nodes, """[ { "from": { "node": "a" }, "to": { "node": "b" } } ]""");
        var after = Document(nodes, """[ { "from": { "node": "a", "output": 0 }, "to": { "node": "b", "input": 0 } }, { "from": { "node": "a", "output": 1 }, "to": { "node": "b" } } ]""");

        var plan = WorkflowDiff.Fill(Empty, before, after);
        var added = Assert.Single(plan.AddedConnections);
        Assert.Equal(new WorkflowPlanConnection("a", "Старт", 1, "b", "Б", 0), added);
        Assert.Empty(plan.RemovedConnections);
        Assert.Empty(plan.ChangedNodes);
    }

    [Fact]
    public void Settings_and_other_parts_are_listed()
    {
        var before = Document($"[{Start}]", settings: """{ "timezone": "UTC", "concurrency": { "max": 1 } }""");
        var after = Document($"[{Start}]", settings: """{ "timezone": "Europe/Kyiv" }""");
        after["stages"] = new JsonArray();

        var plan = WorkflowDiff.Fill(Empty, before, after);
        Assert.Equal(["timezone", "concurrency"], plan.SettingsChanges.Select(c => c.Field));
        Assert.Null(plan.SettingsChanges[1].After);
        Assert.Equal(["stages"], plan.OtherChanges);
    }
}
