using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBP.Shared.Workflow;

namespace OpenBP.Api;

/// <summary>
/// Визначення сценарію в REST (тіла запитів і відповідей) серіалізуються за <see cref="WorkflowJson.Options"/>
/// (docs/architecture.md §10.3): той самий вигляд, що у файлах і MCP, невідомі поля зберігаються.
/// </summary>
public sealed class WorkflowDefinitionJsonConverter : JsonConverter<WorkflowDefinition>
{
    /// <inheritdoc />
    public override WorkflowDefinition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<WorkflowDefinition>(ref reader, WorkflowJson.Options);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, WorkflowDefinition value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, WorkflowJson.Options);
}
