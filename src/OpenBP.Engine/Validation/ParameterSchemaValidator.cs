using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Validation;

namespace OpenBP.Engine.Validation;

/// <summary>
/// Перевірка параметрів вузла за JSON Schema (правило 5 з docs/workflow-format.md §«Валідація»):
/// рядок з виразом <c>{{ }}</c> допустимий замість значення будь-якого типу — порушення в цьому місці ігноруються,
/// бо тип стане відомим лише після обчислення.
/// </summary>
public static class ParameterSchemaValidator
{
    private static readonly ConditionalWeakTable<JsonObject, JsonSchema?> Compiled = [];

    /// <summary>Перевіряє <paramref name="instance"/> схемою <paramref name="schema"/>.</summary>
    /// <param name="schema">JSON Schema (з дескриптора вузла або <c>inputSchema</c> tool'а).</param>
    /// <param name="instance">Значення параметрів; <c>null</c> — як JSON <c>null</c>.</param>
    /// <param name="nodeId">Id вузла для помилок.</param>
    /// <param name="basePath">JSON Pointer, до якого додається шлях порушення, напр. <c>/parameters</c>.</param>
    /// <returns>
    /// Помилки з кодом <see cref="WorkflowValidationCodes.ParameterInvalid"/>. Схема, яку неможливо розібрати,
    /// не блокує збереження — тоді список порожній.
    /// </returns>
    public static IReadOnlyList<WorkflowValidationError> Validate(JsonObject schema, JsonNode? instance, string nodeId, string basePath)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var compiled = Compiled.GetValue(schema, Compile);
        if (compiled is null)
        {
            return [];
        }

        var result = compiled.Evaluate(JsonSerializer.SerializeToElement(instance), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            IncludeApplicatorErrors = false,
        });
        if (result.IsValid)
        {
            return [];
        }

        var expressions = new HashSet<string>(StringComparer.Ordinal);
        CollectExpressions(instance, "", expressions);

        var errors = new List<WorkflowValidationError>();
        var seen = new HashSet<(string, string)>();
        foreach (var detail in result.Details ?? [result])
        {
            if (detail.IsValid || detail.Errors is null)
            {
                continue;
            }

            var location = detail.InstanceLocation.ToString();
            if (expressions.Contains(location))
            {
                continue;
            }

            foreach (var (_, message) in detail.Errors)
            {
                if (seen.Add((location, message)))
                {
                    var path = basePath + location;
                    errors.Add(new(WorkflowValidationCodes.ParameterInvalid, $"{path}: {message}", nodeId, path));
                }
            }
        }

        return errors;
    }

    private static JsonSchema? Compile(JsonObject schema)
    {
        try
        {
            // Власний реєстр: однакові $id у схемах різних вузлів/серверів не конфліктують.
            return JsonSchema.FromText(schema.ToJsonString(), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        catch (Exception ex) when (ex is JsonException or JsonSchemaException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>JSON Pointer'и всіх рядків з виразами <c>{{ }}</c>.</summary>
    private static void CollectExpressions(JsonNode? node, string pointer, HashSet<string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    CollectExpressions(value, $"{pointer}/{key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}", result);
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    CollectExpressions(array[i], $"{pointer}/{i}", result);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && ExpressionSyntax.ContainsExpression(text):
                result.Add(pointer);
                break;
        }
    }
}
