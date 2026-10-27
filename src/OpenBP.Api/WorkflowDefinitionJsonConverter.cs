using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api;

/// <summary>
/// Визначення сценарію в REST (тіла запитів і відповідей) серіалізуються за <see cref="WorkflowJson.Options"/>
/// (docs/architecture.md §10.3): той самий вигляд, що у файлах і MCP, невідомі поля зберігаються.
/// </summary>
public sealed class WorkflowDefinitionJsonConverter(string? defaultTimezone = null) : JsonConverter<WorkflowDefinition>
{
    /// <summary>Пояс нового сценарію, якщо в тілі немає <c>settings.timezone</c> — налаштування інстансу <c>OPENBP_DEFAULT_TIMEZONE</c> (F3-78).</summary>
    public const string DefaultTimezoneKey = "OPENBP_DEFAULT_TIMEZONE";

    /// <summary>Пояс за замовчуванням, якщо змінну не задано.</summary>
    public const string FallbackTimezone = "Europe/Kyiv";

    private readonly string _defaultTimezone = string.IsNullOrWhiteSpace(defaultTimezone) ? FallbackTimezone : defaultTimezone.Trim();

    /// <inheritdoc />
    public override WorkflowDefinition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var definition = document.RootElement.Deserialize<WorkflowDefinition>(WorkflowJson.Options);
        // Без явного пояса сценарій отримує пояс інстансу, а не UTC із формату; явний `UTC` лишається.
        if (definition is not null && !HasTimezone(document.RootElement))
        {
            definition.Settings.Timezone = _defaultTimezone;
        }

        return definition;
    }

    private static bool HasTimezone(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("settings", out var settings)
        && settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty("timezone", out var timezone)
        && timezone.ValueKind == JsonValueKind.String;

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WorkflowDefinition value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, WorkflowJson.Options);
}
