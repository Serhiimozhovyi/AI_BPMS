using System.Text.Json.Nodes;
using OpenBP.Application.Forms;

namespace OpenBP.Application.Tests;

/// <summary>F11-07: поле-телефон форми (<c>format: "phone"</c>) — нормалізація до цифр і перевірка кількості цифр.</summary>
public class FormPhoneTests
{
    private static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "required": ["phone"],
          "properties": {
            "phone": { "type": "string", "format": "phone", "title": "Телефон" },
            "note": { "type": "string" },
            "kind": { "type": "string", "enum": ["a", "b"], "x-openbp-ui": { "widget": "radio" } },
            "tags": { "type": "array", "items": { "enum": ["x", "y"] }, "x-openbp-ui": { "widget": "chips" } }
          }
        }
        """)!.AsObject();

    [Theory]
    [InlineData("+380 67 123 45 67", "380671234567")]
    [InlineData("+38 (067) 123-45-67", "380671234567")]
    [InlineData("0671234567", "0671234567")]
    public void Phone_is_normalized_to_digits(string input, string expected)
    {
        var fields = FormSchema.NormalizePhones(Schema, new JsonObject { ["phone"] = input, ["note"] = "+380 не телефон" });

        Assert.Equal(expected, fields["phone"]!.GetValue<string>());
        Assert.Equal("+380 не телефон", fields["note"]!.GetValue<string>());
    }

    [Fact]
    public void Normalized_phone_passes_and_widgets_do_not_affect_validation()
    {
        var fields = FormSchema.NormalizePhones(Schema, JsonNode.Parse("""{ "phone": "+380 67 123 45 67", "kind": "a", "tags": ["x", "y"] }""")!.AsObject());

        Assert.Empty(FormSchema.Validate(Schema, fields));
        Assert.Null(FormSchema.CompileError(Schema));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("1234567890123456")]
    public void Too_short_or_long_phone_is_an_error(string input)
    {
        var fields = FormSchema.NormalizePhones(Schema, new JsonObject { ["phone"] = input });

        var error = Assert.Single(FormSchema.Validate(Schema, fields));
        Assert.Equal("phone", error.Field);
        Assert.Contains("номер телефону", error.Message, StringComparison.Ordinal);
    }
}
