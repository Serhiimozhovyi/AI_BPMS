using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBP.Shared.Workflow;

/// <summary>(Де)серіалізація формату workflow і доступ до його JSON Schema.</summary>
public static class WorkflowJson
{
    private const string SchemaResource = "OpenBP.Shared.Schemas.workflow.schema.json";

    private static readonly Lazy<string> SchemaText = new(LoadSchema);

    /// <summary>Налаштування System.Text.Json для формату: camelCase, без null-полів (крім явно позначених).</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Текст JSON Schema формату (<c>Schemas/workflow.schema.json</c>).</summary>
    public static string Schema => SchemaText.Value;

    public static WorkflowDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(json, Options)
        ?? throw new JsonException("Порожній документ workflow.");

    public static string Serialize(WorkflowDefinition workflow) =>
        JsonSerializer.Serialize(workflow, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private static string LoadSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(SchemaResource)
            ?? throw new InvalidOperationException($"Ресурс {SchemaResource} не знайдено.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
