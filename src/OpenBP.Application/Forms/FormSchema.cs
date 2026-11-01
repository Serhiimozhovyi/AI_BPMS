using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OpenBP.Application.Errors;

namespace OpenBP.Application.Forms;

/// <summary>
/// Перевірка надісланих полів за JSON Schema форми (F6-03) з поясненнями українською за полями. Поле-файл —
/// властивість <c>{ "type": "string", "format": "binary" }</c>: у <c>fields</c> воно містить назву файлу.
/// Порожні рядки й <c>null</c> вважаються незаповненими полями.
/// </summary>
internal static class FormSchema
{
    /// <summary>Значення <c>format</c> поля-файлу.</summary>
    public const string BinaryFormat = "binary";

    /// <summary>Значення <c>format</c> поля-телефону (F11-07): у даних — лише цифри, від 10 до 15.</summary>
    public const string PhoneFormat = "phone";

    private const int PhoneMinDigits = 10;
    private const int PhoneMaxDigits = 15;

    /// <summary>
    /// Поля-телефони (<c>format: "phone"</c>) — лише цифрами: «+380 67 123-45-67» → <c>380671234567</c>, як колонка
    /// <c>format: "phone"</c> сторінки (F8-15) і маска форми на клієнті (<c>forms/phone.ts</c>).
    /// </summary>
    /// <param name="schema">Схема форми.</param>
    /// <param name="fields">Надіслані поля.</param>
    public static JsonObject NormalizePhones(JsonObject schema, JsonObject fields)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return fields;
        }

        var result = fields.DeepClone().AsObject();
        foreach (var (name, property) in properties)
        {
            if (IsPhone(property) && result[name] is JsonValue v && v.TryGetValue<string>(out var text))
            {
                result[name] = new string([.. text.Where(char.IsAsciiDigit)]);
            }
        }

        return result;
    }

    private static bool IsPhone(JsonNode? property) =>
        property is JsonObject p && p["format"] is JsonValue f && f.TryGetValue<string>(out var format) && format == PhoneFormat;

    /// <summary>Назви властивостей-файлів схеми верхнього рівня.</summary>
    /// <param name="schema">Схема форми.</param>
    public static HashSet<string> FileFields(JsonObject schema)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, property) in properties)
            {
                if (property is JsonObject p && p["format"] is JsonValue f && f.TryGetValue<string>(out var format) && format == BinaryFormat)
                {
                    result.Add(name);
                }
            }
        }

        return result;
    }

    /// <summary>Поля без незаповнених значень (<c>null</c>, порожній рядок).</summary>
    /// <param name="fields">Надіслані поля.</param>
    public static JsonObject WithoutEmpty(JsonObject fields)
    {
        var result = new JsonObject();
        foreach (var (name, value) in fields)
        {
            if (value is null || (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0))
            {
                continue;
            }

            result[name] = value.DeepClone();
        }

        return result;
    }

    /// <summary>Помилка побудови схеми форми або <c>null</c>, якщо схема коректна (перевірка при активації сценарію).</summary>
    /// <param name="schema">Схема форми.</param>
    public static string? CompileError(JsonObject schema)
    {
        try
        {
            Compile(schema);
            return FormLayout.Error(schema) is { } layoutError ? $"Розмітка форми: {layoutError}" : null;
        }
        catch (ConflictException ex)
        {
            return ex.Message;
        }
    }

    private static JsonSchema Compile(JsonObject schema)
    {
        if (schema["type"] is JsonValue t && t.TryGetValue<string>(out var type) && type != "object")
        {
            throw new ConflictException("FORM_SCHEMA_INVALID", $"Поля форми мають бути об'єктом (type: object), а задано «{type}».");
        }

        try
        {
            return JsonSchema.FromText(WithoutAnnotations(schema).ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException or InvalidOperationException)
        {
            throw new ConflictException("FORM_SCHEMA_INVALID", $"Поля форми задано некоректною JSON Schema: {ex.Message} Виправте параметр schema вузла.");
        }
    }

    /// <summary>
    /// Копія схеми без анотацій показу <c>x-openbp-*</c> (F11-01: <c>x-openbp-layout</c> на корені, <c>x-openbp-ui</c> на
    /// властивостях): бібліотека JSON Schema відкидає невідомі ключові слова, а на дані вони не впливають.
    /// </summary>
    private static JsonObject WithoutAnnotations(JsonObject schema)
    {
        var copy = schema.DeepClone().AsObject();
        Strip(copy);
        if (copy["properties"] is JsonObject properties)
        {
            foreach (var (_, property) in properties)
            {
                if (property is JsonObject p)
                {
                    Strip(p);
                }
            }
        }

        return copy;

        static void Strip(JsonObject node)
        {
            foreach (var key in node.Select(p => p.Key).Where(k => k.StartsWith("x-openbp-", StringComparison.Ordinal)).ToList())
            {
                node.Remove(key);
            }
        }
    }

    /// <summary>
    /// Перевіряє поля; повертає помилки за полями (порожній список — усе гаразд). Поля, приховані умовами
    /// «Показувати, якщо» (F11-06), не перевіряються й не обов'язкові — викликач відкидає їх <see cref="VisibleIf.WithoutHidden"/>.
    /// </summary>
    /// <param name="schema">Схема форми.</param>
    /// <param name="fields">Поля без незаповнених.</param>
    /// <exception cref="ConflictException">Схема форми сама некоректна (<c>FORM_SCHEMA_INVALID</c>).</exception>
    public static List<FormFieldError> Validate(JsonObject schema, JsonObject fields)
    {
        var hidden = VisibleIf.HiddenFields(schema, fields);
        fields = VisibleIf.WithoutHidden(schema, fields);
        var compiled = Compile(hidden.Count == 0 ? schema : WithoutRequired(schema, hidden));
        var errors = new List<FormFieldError>();
        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var n) ? n : null).OfType<string>())
            {
                if (!fields.ContainsKey(name) && !hidden.Contains(name))
                {
                    errors.Add(new(name, "Заповніть це поле."));
                }
            }
        }

        if (schema["properties"] is JsonObject phoneProperties)
        {
            foreach (var (name, property) in phoneProperties)
            {
                if (IsPhone(property) && fields[name] is JsonValue pv && pv.TryGetValue<string>(out var phone)
                    && (phone.Length is < PhoneMinDigits or > PhoneMaxDigits || !phone.All(char.IsAsciiDigit)))
                {
                    errors.Add(new(name, "Вкажіть номер телефону, напр. +380 67 123 45 67."));
                }
            }
        }

        var result = compiled.Evaluate(JsonSerializer.SerializeToElement(fields), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            IncludeApplicatorErrors = false,
            RequireFormatValidation = true,
        });
        if (result.IsValid)
        {
            return errors;
        }

        var seen = new HashSet<string>(errors.Select(e => e.Field), StringComparer.Ordinal);
        foreach (var detail in result.Details ?? [result])
        {
            if (detail.IsValid || detail.Errors is null)
            {
                continue;
            }

            var location = detail.InstanceLocation.ToString();
            var field = location.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? Unescape(first) : "";
            foreach (var (keyword, _) in detail.Errors)
            {
                if (keyword == "required" || !seen.Add(field))
                {
                    continue;
                }

                errors.Add(new(field, Message(keyword, field.Length == 0 ? schema : PropertySchema(schema, field))));
            }
        }

        if (errors.Count == 0)
        {
            errors.Add(new("", "Дані форми не відповідають її полям."));
        }

        return errors;
    }

    /// <summary>Копія схеми, де приховані поля прибрано з <c>required</c>.</summary>
    private static JsonObject WithoutRequired(JsonObject schema, HashSet<string> hidden)
    {
        var copy = schema.DeepClone().AsObject();
        if (copy["required"] is JsonArray required)
        {
            copy["required"] = new JsonArray([.. required.Where(r => !(r is JsonValue v && v.TryGetValue<string>(out var n) && hidden.Contains(n))).Select(r => r?.DeepClone())]);
        }

        return copy;
    }

    private static string Unescape(string segment) => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static JsonObject? PropertySchema(JsonObject schema, string field) => schema["properties"]?[field] as JsonObject;

    private static string Message(string keyword, JsonObject? property)
    {
        string? Number(string name) => property?[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(CultureInfo.InvariantCulture) : null;
        return keyword switch
        {
            "type" => property?["type"]?.ToString() switch
            {
                "integer" => "Вкажіть ціле число.",
                "number" => "Вкажіть число.",
                "boolean" => "Оберіть так чи ні.",
                "array" => "Оберіть одне чи кілька значень.",
                _ => "Значення має неправильний тип.",
            },
            "minLength" => $"Щонайменше {Number("minLength")} символів.",
            "maxLength" => $"Не більше {Number("maxLength")} символів.",
            "minimum" or "exclusiveMinimum" => $"Значення має бути не менше {Number("minimum") ?? Number("exclusiveMinimum")}.",
            "maximum" or "exclusiveMaximum" => $"Значення має бути не більше {Number("maximum") ?? Number("exclusiveMaximum")}.",
            "enum" or "const" => "Оберіть одне зі значень списку.",
            "pattern" => "Значення не відповідає потрібному формату.",
            "format" => property?["format"]?.ToString() switch
            {
                "email" => "Вкажіть коректну адресу пошти.",
                "date" => "Вкажіть дату.",
                "date-time" => "Вкажіть дату й час.",
                "uri" => "Вкажіть повну адресу, напр. https://example.com.",
                _ => "Значення не відповідає потрібному формату.",
            },
            "additionalProperties" or "unevaluatedProperties" or "false" => "Форма не має такого поля.",
            "minItems" => $"Оберіть щонайменше {Number("minItems")}.",
            "maxItems" => $"Оберіть не більше {Number("maxItems")}.",
            _ => "Значення некоректне.",
        };
    }
}
