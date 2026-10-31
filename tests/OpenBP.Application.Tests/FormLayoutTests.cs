using System.Text.Json.Nodes;
using OpenBP.Application.Forms;

namespace OpenBP.Application.Tests;

/// <summary>F11-01: перевірка розмітки форми <c>x-openbp-layout</c> і підказок <c>x-openbp-ui</c>.</summary>
public class FormLayoutTests
{
    private static JsonObject Schema(string layout, string? nameUi = null) => JsonNode.Parse($$"""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string"{{(nameUi is null ? "" : ", \"x-openbp-ui\": " + nameUi)}} },
            "from": { "type": "string", "format": "date" },
            "to": { "type": "string", "format": "date" },
            "reason": { "type": "string" }
          }{{(layout.Length == 0 ? "" : ", \"x-openbp-layout\": " + layout)}}
        }
        """)!.AsObject();

    [Fact]
    public void Schema_without_layout_or_with_valid_layout_has_no_error()
    {
        Assert.Null(FormLayout.Error(Schema("")));
        Assert.Null(FormLayout.Error(Schema("""
            { "sections": [
              { "title": "Хто", "rows": [[{ "field": "name" }]] },
              { "title": "Коли", "collapsible": true, "collapsed": true, "visibleIf": { "field": "name", "operator": "isNotEmpty" },
                "rows": [[{ "field": "from", "width": 6 }, { "field": "to", "width": 6 }], [{ "field": "reason", "visibleIf": { "field": "name", "operator": "eq", "value": "x" } }]] }
            ] }
            """, """{ "widget": "radio", "placeholder": "Коротко", "rows": 2 }""")));
    }

    [Theory]
    [InlineData("""[]""", "x-openbp-layout: очікується")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "ghost" }]] }] }""", "x-openbp-layout/sections/0/rows/0/0: поля «ghost» немає")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "name" }]] }, { "rows": [[{ "field": "name" }]] }] }""", "sections/1/rows/0/0: поле «name» у розмітці двічі")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "from", "width": 13 }]] }] }""", "rows/0/0/width: ціле від 1 до 12")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "from", "width": 8 }, { "field": "to", "width": 6 }]] }] }""", "rows/0: сума ширин 14 більша за 12")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "name" }, { "field": "from" }, { "field": "to" }, { "field": "reason" }, { "field": "name" }]] }] }""", "у рядку не більше 4 полів")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "name", "visibleIf": { "field": "name", "operator": "eq" } }]] }] }""", "не може залежати від нього самого")]
    [InlineData("""{ "sections": [{ "rows": [[{ "field": "name", "visibleIf": { "field": "from", "operator": "between" } }]] }] }""", "visibleIf/operator: очікується")]
    [InlineData("""{ "sections": [{ "collapsible": "так", "rows": [] }] }""", "sections/0/collapsible: true або false")]
    [InlineData("""{ "sections": [{ "title": 5, "rows": [] }] }""", "sections/0/title: рядок")]
    [InlineData("""{ "sections": [{ "title": "Без рядків" }] }""", "sections/0/rows: очікується масив")]
    public void Invalid_layout_is_reported_with_path(string layout, string expected)
    {
        var error = FormLayout.Error(Schema(layout));

        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "widget": "slider" }""", "properties/name/x-openbp-ui/widget")]
    [InlineData("""{ "rows": 50 }""", "properties/name/x-openbp-ui/rows")]
    [InlineData("\"radio\"", "properties/name/x-openbp-ui: очікується об'єкт")]
    public void Invalid_field_ui_is_reported(string ui, string expected)
    {
        Assert.Contains(expected, FormLayout.Error(Schema("", ui)), StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_of_page_form_is_checked_against_given_fields()
    {
        var layout = JsonNode.Parse("""{ "sections": [{ "rows": [[{ "field": "client" }, { "field": "status" }]] }] }""");

        Assert.Null(FormLayout.Error(layout, new HashSet<string> { "client", "status" }, "formLayout"));
        Assert.Equal("formLayout/sections/0/rows/0/1: поля «status» немає у формі.", FormLayout.Error(layout, new HashSet<string> { "client" }, "formLayout"));
    }
}
