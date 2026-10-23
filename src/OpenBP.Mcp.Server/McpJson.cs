using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBP.Application.Executions;

namespace OpenBP.Mcp.Server;

/// <summary>
/// JSON відповідей MCP-сервера: camelCase, переліки рядками, без <c>null</c>-полів — як у REST; стани виконань — snake_case
/// (<c>timed_out</c>), як у БД і REST.
/// </summary>
public static class McpJson
{
    /// <summary>Налаштування серіалізації.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter<ExecutionState>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
