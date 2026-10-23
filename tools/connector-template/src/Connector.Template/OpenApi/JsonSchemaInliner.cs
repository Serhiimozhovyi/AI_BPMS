using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Connector.Template.OpenApi;

/// <summary>
/// Схема OpenAPI → самодостатня JSON Schema для <c>inputSchema</c>: серіалізує схему як OpenAPI 3.1 (це JSON Schema
/// 2020-12) і підставляє <c>$ref</c> на <c>#/components/schemas/…</c>, бо в tool'і немає документа, на який посилатися.
/// Циклічне посилання стає <c>{}</c> (будь-яке значення) з описом.
/// </summary>
/// <param name="document">Документ із <c>components.schemas</c>.</param>
internal sealed class JsonSchemaInliner(OpenApiDocument document)
{
    private const string ComponentsPrefix = "#/components/schemas/";

    private readonly Dictionary<string, JsonNode?> _components = [];

    /// <summary>JSON Schema без зовнішніх посилань.</summary>
    /// <param name="schema">Схема OpenAPI.</param>
    public JsonNode Inline(IOpenApiSchema schema) => Resolve(Serialize(schema), []) ?? new JsonObject();

    private static JsonNode? Serialize(IOpenApiSerializable schema)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        schema.SerializeAsV31(new OpenApiJsonWriter(text));
        return JsonNode.Parse(text.ToString());
    }

    private JsonNode? Resolve(JsonNode? node, Stack<string> path)
    {
        switch (node)
        {
            case JsonObject obj when obj["$ref"]?.GetValue<string>() is { } reference:
                var name = reference.StartsWith(ComponentsPrefix, StringComparison.Ordinal) ? reference[ComponentsPrefix.Length..] : null;
                JsonObject resolved;
                if (name is null || path.Contains(name) || Component(name) is not JsonObject target)
                {
                    resolved = new JsonObject { ["description"] = $"See {name ?? reference}." };
                }
                else
                {
                    path.Push(name);
                    resolved = (JsonObject)Resolve(target.DeepClone(), path)!;
                    path.Pop();
                }

                // OpenAPI 3.1 дозволяє ключові слова поруч із $ref (напр. description) — вони уточнюють ціль.
                foreach (var (key, value) in obj.ToList())
                {
                    if (key != "$ref")
                    {
                        resolved[key] = Resolve(value?.DeepClone(), path);
                    }
                }

                return resolved;
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    obj[key] = Resolve(value?.DeepClone(), path);
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Resolve(array[i]?.DeepClone(), path);
                }

                return array;
            default:
                return node;
        }
    }

    private JsonNode? Component(string name)
    {
        if (!_components.TryGetValue(name, out var node))
        {
            node = document.Components?.Schemas?.TryGetValue(name, out var schema) == true ? Serialize(schema) : null;
            _components[name] = node;
        }

        return node;
    }
}
