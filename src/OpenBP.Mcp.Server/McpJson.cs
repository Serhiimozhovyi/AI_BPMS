using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBP.Application.Executions;

namespace OpenBP.Mcp.Server;

/// <summary>
/// JSON відповідей MCP-сервера: camelCase, переліки рядками; стани виконань — snake_case (<c>timed_out</c>), як у БД і REST.
/// <c>null</c>-поля пишуться: <c>outputSchema</c> tools робить nullable-властивості записів обов'язковими, і клієнти на
/// TypeScript SDK (Claude Desktop, Claude Code) відкидають <c>structuredContent</c> без них.
/// </summary>
public static class McpJson
{
    /// <summary>Налаштування серіалізації.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter<ExecutionState>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
