using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using OpenBP.Shared.Projects;

namespace OpenBP.Shared.Tests;

/// <summary>
/// F8-14: JSON Schema сторінки застосунку приймає всі приклади з docs/projects.md §3 (сторінки й оболонку <c>app</c>) і
/// відхиляє помилкові визначення: зайву властивість, невідомий вид, список без колонок, форму з <c>fields</c> і
/// <c>keyValue</c> разом, блок без сторінки й визначення, невідомий колір.
/// </summary>
public partial class ProjectPageSchemaTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(ProjectPageJson.Schema);

    private static readonly EvaluationOptions Options = BuildOptions();

    private static EvaluationOptions BuildOptions()
    {
        SchemaRegistry.Global.Register(Schema);
        return new EvaluationOptions { OutputFormat = OutputFormat.List };
    }

    /// <summary>Усі JSON-приклади розділу 3 документа: сторінки (з <c>definition</c>) і оболонка (з <c>nav</c>).</summary>
    public static IEnumerable<object[]> DocExamples()
    {
        var text = File.ReadAllText(Path.Combine(WorkflowTestData.RepoRoot(), "docs", "projects.md"));
        var section = text[text.IndexOf("## 3. Сторінки застосунку", StringComparison.Ordinal)..text.IndexOf("## 4. Ролі застосунку", StringComparison.Ordinal)];
        foreach (Match block in JsonBlock().Matches(section))
        {
            var node = JsonNode.Parse(block.Groups[1].Value) as JsonObject;
            if (node is not null && (node.ContainsKey("definition") || node.ContainsKey("nav")))
            {
                yield return [node["key"]?.GetValue<string>() ?? "app", node.ToJsonString()];
            }
        }
    }

    private static EvaluationResults Evaluate(JsonNode node, JsonSchema? schema = null)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return (schema ?? Schema).Evaluate(doc.RootElement, Options);
    }

    private static string Errors(EvaluationResults results) =>
        string.Join("; ", (results.Details ?? []).Where(d => d.Errors is { Count: > 0 }).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}")));

    private static JsonObject Page(string kind, string definition) =>
        new() { ["name"] = "Сторінка", ["kind"] = kind, ["definition"] = JsonNode.Parse(definition) };

    [Fact]
    public void Schema_is_embedded_and_declares_draft_2020()
    {
        var schema = JsonNode.Parse(ProjectPageJson.Schema)!;

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal(["list", "detail", "form", "metrics", "calendar", "dashboard"], schema["properties"]!["kind"]!["enum"]!.AsArray().Select(k => k!.GetValue<string>()));
    }

    [Theory]
    [MemberData(nameof(DocExamples))]
    public void Every_example_in_the_docs_is_valid(string key, string json)
    {
        var node = JsonNode.Parse(json)!;
        var schema = key == "app"
            ? JsonSchema.FromText("""{ "$ref": "https://openbp.dev/schemas/project-page.schema.json#/$defs/app" }""")
            : Schema;

        var results = Evaluate(node, schema);

        Assert.True(results.IsValid, $"{key}: {Errors(results)}");
    }

    [Fact]
    public void Docs_have_an_example_of_every_kind()
    {
        var kinds = DocExamples().Select(e => JsonNode.Parse((string)e[1])!["kind"]?.GetValue<string>()).Where(k => k is not null).Distinct().ToList();

        Assert.Equal(["calendar", "dashboard", "detail", "form", "list", "metrics"], kinds.Order());
    }

    [Theory]
    [InlineData("list", """{ "table": "events", "columns": [{ "column": "title" }], "filtres": [] }""", "filtres")]
    [InlineData("list", """{ "table": "events" }""", "columns")]
    [InlineData("list", """{ "table": "events", "columns": [{ "column": "status", "format": "badge", "colors": { "x": "pink" } }] }""", "pink")]
    [InlineData("list", """{ "table": "events", "columns": [{ "column": "title" }], "filters": [{ "column": "status", "kind": "toggle" }] }""", "toggle")]
    [InlineData("list", """{ "table": "events", "columns": [{ "column": "title" }], "actions": [{ "label": "x", "page": "a", "workflow": "b" }] }""", "page")]
    [InlineData("list", """{ "table": "events", "columns": [{ "column": "title" }], "create": { "page": "a", "mode": "popup" } }""", "popup")]
    [InlineData("form", """{ "table": "settings", "fields": [{ "column": "key" }], "keyValue": { "key": "key", "value": "value" }, "onSubmit": { "operation": "upsert" } }""", "keyValue")]
    [InlineData("form", """{ "table": "settings", "onSubmit": { "operation": "upsert" } }""", "fields")]
    [InlineData("calendar", """{ "table": "slots", "calendar": { "start": "starts_at", "views": ["year"] } }""", "year")]
    [InlineData("calendar", """{ "table": "slots" }""", "calendar")]
    [InlineData("dashboard", """{ "blocks": [{ "kind": "list" }] }""", "list")]
    [InlineData("dashboard", """{ "blocks": [{ "kind": "text" }] }""", "text")]
    [InlineData("dashboard", """{ "blocks": [{ "kind": "list", "page": "a", "limit": 500 }] }""", "500")]
    [InlineData("dashboard", """{ "blocks": [] }""", "blocks")]
    public void Wrong_definitions_are_rejected(string kind, string definition, string hint)
    {
        var results = Evaluate(Page(kind, definition));

        Assert.False(results.IsValid, $"{kind} з «{hint}» мало б не пройти: {definition}");
    }

    [Theory]
    [InlineData("""{ "name": "x", "kind": "report", "definition": {} }""")]
    [InlineData("""{ "name": "x", "kind": "list", "definition": { "table": "t", "columns": [{ "column": "a" }] }, "roles": ["Coordinator"] }""")]
    [InlineData("""{ "name": "", "kind": "list", "definition": { "table": "t", "columns": [{ "column": "a" }] } }""")]
    [InlineData("""{ "name": "x", "kind": "list", "definition": { "table": "t", "columns": [{ "column": "a" }] }, "id": "1" }""")]
    public void Wrong_pages_are_rejected(string page)
    {
        Assert.False(Evaluate(JsonNode.Parse(page)!).IsValid, page);
    }

    [Theory]
    [InlineData("""{ "title": "Кабінет", "nav": [{ "label": "", "pages": ["a", "a"] }] }""", true)]
    [InlineData("""{ "title": "Кабінет", "nav": [{ "label": "x", "pages": [] }] }""", false)]
    [InlineData("""{ "nav": [] }""", false)]
    [InlineData("""{ "title": "Кабінет", "menu": [] }""", false)]
    public void App_shell_is_checked_by_shape_only(string app, bool valid)
    {
        // Повтор сторінки в меню — перевірка сервера (ProjectApps.Validate), схема такого не бачить.
        var schema = JsonSchema.FromText("""{ "$ref": "https://openbp.dev/schemas/project-page.schema.json#/$defs/app" }""");

        Assert.Equal(valid, Evaluate(JsonNode.Parse(app)!, schema).IsValid);
    }

    [GeneratedRegex("```json\\s*\\n(.*?)\\n```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();
}
