using System.Reflection;
using System.Text.Json.Nodes;

namespace OpenBP.Nodes.Core;

/// <summary>Завантажує JSON Schema параметрів <c>core.*</c> вузлів із вбудованих ресурсів <c>Schemas/*.schema.json</c>.</summary>
internal static class NodeSchemas
{
    /// <summary>Читає схему за коротким ім'ям, напр. <c>if</c> → <c>Schemas/if.schema.json</c>.</summary>
    /// <param name="name">Ім'я файлу схеми без <c>.schema.json</c>.</param>
    public static JsonObject Load(string name)
    {
        var resource = $"OpenBP.Nodes.Core.Schemas.{name}.schema.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Ресурс {resource} не знайдено.");
        return JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException($"Ресурс {resource} не є JSON-об'єктом.");
    }
}
