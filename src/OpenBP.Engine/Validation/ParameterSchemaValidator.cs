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
public static partial class ParameterSchemaValidator
{
    private static readonly ConditionalWeakTable<JsonObject, JsonSchema?> Compiled = [];

    /// <summary>
    /// Тексти порушень українською за правилом «що сталося → що зробити» (F3-57). JsonSchema.Net тримає шаблони
    /// глобально; інших користувачів у процесах AI_BPMS немає (схеми tool'ів і outputSchema — теж через нього).
    /// </summary>
    static ParameterSchemaValidator()
    {
        ErrorMessages.Required = "Не заповнено обов'язкові поля: [[missing]]. Вкажіть значення або вираз.";
        ErrorMessages.Type = "Значення має тип [[received]], а потрібен [[expected]]. Змініть значення або задайте вираз.";
        ErrorMessages.Enum = "Значення не з дозволеного списку. Оберіть один із варіантів.";
        ErrorMessages.Const = "Значення має дорівнювати [[value]]. Вкажіть саме його.";
        ErrorMessages.Minimum = "Значення [[received]] менше за мінімум [[limit]]. Вкажіть не менше [[limit]].";
        ErrorMessages.ExclusiveMinimum = "Значення [[received]] має бути більшим за [[limit]]. Збільште його.";
        ErrorMessages.Maximum = "Значення [[received]] більше за максимум [[limit]]. Вкажіть не більше [[limit]].";
        ErrorMessages.ExclusiveMaximum = "Значення [[received]] має бути меншим за [[limit]]. Зменште його.";
        ErrorMessages.MinLength = "Текст коротший за [[limit]] символів. Допишіть значення.";
        ErrorMessages.MaxLength = "Текст довший за [[limit]] символів. Скоротіть значення.";
        ErrorMessages.MinItems = "Елементів [[received]], а потрібно щонайменше [[limit]]. Додайте елементи.";
        ErrorMessages.MaxItems = "Елементів [[received]], а дозволено щонайбільше [[limit]]. Приберіть зайві.";
        ErrorMessages.Pattern = "Значення не відповідає формату [[pattern]]. Виправте значення.";
        ErrorMessages.AdditionalProperties = "Невідомі поля: [[failed]]. Приберіть їх.";
        ErrorMessages.Format = "Значення не у форматі [[format]]. Виправте значення.";
    }

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

            foreach (var message in detail.Errors.Values.Select(Names))
            {
                if (seen.Add((location, message)))
                {
                    var path = basePath + location;
                    var text = location.Length == 0 ? message : $"Параметр «{Readable(location)}»: {message}";
                    errors.Add(new(WorkflowValidationCodes.ParameterInvalid, text, nodeId, path));
                }
            }
        }

        return errors;
    }

    /// <summary>Списки назв полів із шаблонів (<c>["a","b"]</c>) → <c>«a», «b»</c>.</summary>
    private static string Names(string message) =>
        NameList().Replace(message, m => string.Join(", ", m.Groups[1].Value.Split(',').Select(n => $"«{n.Trim().Trim('"')}»")));

    [System.Text.RegularExpressions.GeneratedRegex("""\[("[^"\]]*"(?:,\s*"[^"\]]*")*)\]""")]
    private static partial System.Text.RegularExpressions.Regex NameList();

    /// <summary>JSON Pointer → шлях для людини: <c>/arguments/chat_id</c> → <c>arguments.chat_id</c>, індекси — <c>[0]</c>.</summary>
    private static string Readable(string pointer)
    {
        var result = new System.Text.StringBuilder();
        foreach (var segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                result.Append('[').Append(name).Append(']');
            }
            else
            {
                result.Append(result.Length == 0 ? "" : ".").Append(name);
            }
        }

        return result.ToString();
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
