using System.Text.Json.Nodes;
using OpenBP.Application.Errors;
using OpenBP.Application.Forms;

namespace OpenBP.Application.Tests;

/// <summary>
/// F11-06: умова «Показувати, якщо». Кейси <see cref="VisibleIf.IsVisible"/> — зі спільної таблиці
/// <c>tests/form-cases/visible-if.cases.json</c>, яку читає й Vitest (<c>visible-if.spec.ts</c>).
/// </summary>
public class VisibleIfTests
{
    private static JsonArray Cases() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "form-cases", "visible-if.cases.json")))!["cases"]!.AsArray();

    /// <summary>Назви кейсів спільної таблиці.</summary>
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases())
        {
            data.Add(c!["name"]!.GetValue<string>());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Shared_case(string name)
    {
        var c = Cases().Single(x => x!["name"]!.GetValue<string>() == name)!;

        Assert.Equal(c["expected"]!.GetValue<bool>(), VisibleIf.IsVisible(c["rule"]!.AsObject(), c["values"]!.AsObject()));
    }

    [Fact]
    public void Shared_table_covers_every_operator()
    {
        var operators = Cases().Select(c => c!["rule"]!["operator"]!.GetValue<string>()).ToHashSet();

        Assert.Superset(new HashSet<string> { "eq", "ne", "contains", "isEmpty", "isNotEmpty", "gt", "lt" }, operators);
    }

    private static readonly JsonObject Leave = JsonNode.Parse("""
        {
          "type": "object",
          "required": ["kind", "reason", "doctor"],
          "properties": {
            "kind": { "type": "string", "enum": ["annual", "other", "sick"] },
            "reason": { "type": "string", "minLength": 3 },
            "doctor": { "type": "string" },
            "note": { "type": "string" }
          },
          "x-openbp-layout": { "sections": [
            { "rows": [[{ "field": "kind" }], [{ "field": "reason", "visibleIf": { "field": "kind", "operator": "eq", "value": "other" } }]] },
            { "title": "Лікарняний", "visibleIf": { "field": "kind", "operator": "eq", "value": "sick" }, "rows": [[{ "field": "doctor" }]] },
            { "rows": [[{ "field": "note", "visibleIf": { "field": "reason", "operator": "isNotEmpty" } }]] }
          ] }
        }
        """)!.AsObject();

    [Fact]
    public void Hidden_fields_follow_field_and_section_rules_and_chains()
    {
        var values = JsonNode.Parse("""{ "kind": "annual", "reason": "щось", "doctor": "Іваненко", "note": "x" }""")!.AsObject();

        // reason приховано умовою поля, doctor — секції, note — бо reason (джерело) приховане й вважається порожнім.
        Assert.Equal(["doctor", "note", "reason"], VisibleIf.HiddenFields(Leave, values).Order(StringComparer.Ordinal));
        Assert.Equal(["kind"], VisibleIf.WithoutHidden(Leave, values).Select(p => p.Key));
    }

    [Fact]
    public void Schema_without_layout_hides_nothing()
    {
        var schema = JsonNode.Parse("""{ "type": "object", "properties": { "a": { "type": "string" } } }""")!.AsObject();

        Assert.Empty(VisibleIf.HiddenFields(schema, []));
    }

    [Fact]
    public void Hidden_required_field_does_not_block_and_hidden_value_is_not_validated()
    {
        // reason прихована (kind ≠ other): обов'язковість не діє, а закоротке значення не перевіряється.
        var errors = FormSchema.Validate(Leave, JsonNode.Parse("""{ "kind": "annual", "reason": "x" }""")!.AsObject());

        Assert.Empty(errors);
    }

    [Fact]
    public void Visible_required_field_still_blocks()
    {
        var errors = FormSchema.Validate(Leave, JsonNode.Parse("""{ "kind": "other" }""")!.AsObject());

        Assert.Equal(["reason"], errors.Select(e => e.Field));
    }

    [Fact]
    public void Visible_field_value_is_still_validated()
    {
        var errors = FormSchema.Validate(Leave, JsonNode.Parse("""{ "kind": "other", "reason": "x" }""")!.AsObject());

        Assert.Equal([new FormFieldError("reason", "Щонайменше 3 символів.")], errors);
    }
}
