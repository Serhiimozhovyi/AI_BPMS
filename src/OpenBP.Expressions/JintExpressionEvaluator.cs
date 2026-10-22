using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using OpenBP.Engine;
using OpenBP.Engine.Expressions;
using OpenBP.Shared.Workflow;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace OpenBP.Expressions;

/// <summary>
/// <see cref="IExpressionEvaluator"/> на Jint (docs/architecture.md §5): вирази <c>{{ &lt;js&gt; }}</c> у рядках,
/// змінні <c>$json</c>, <c>$input</c>, <c>$node</c>, <c>$now</c>, <c>$env</c>, <c>$execution</c>.
/// Пісочниця: ліміти часу, пам'яті і рекурсії з <see cref="JintExpressionOptions"/>; без CLR-interop,
/// без <c>require</c>/модулів, без <c>eval</c> і <c>new Function</c> з рядка.
/// Кожен виклик створює окремий рушій, тож обчислювач потокобезпечний і вирази не діляться станом.
/// </summary>
public sealed class JintExpressionEvaluator : IExpressionEvaluator
{
    private const string InputFactory =
        "(function (items, index) { return { item: items[index], all: function () { return items; }, " +
        "first: function () { return items[0]; }, last: function () { return items[items.length - 1]; } }; })";

    private readonly JintExpressionOptions _options;

    /// <summary>Створює обчислювач із лімітами за замовчуванням або заданими.</summary>
    /// <param name="options">Ліміти пісочниці; <c>null</c> — <see cref="JintExpressionOptions.Default"/>.</param>
    public JintExpressionEvaluator(JintExpressionOptions? options = null)
    {
        _options = options ?? JintExpressionOptions.Default;
    }

    /// <inheritdoc />
    public JsonNode? Evaluate(string expression, ExpressionScope scope)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);

        if (!ExpressionSyntax.ContainsExpression(expression))
        {
            return JsonValue.Create(expression);
        }

        var engine = CreateEngine(expression, scope);

        if (ExpressionSyntax.IsSingleExpression(expression))
        {
            var value = Run(engine, expression, ExpressionTemplate.SingleBody(expression));
            return ToJsonNode(engine, expression, value);
        }

        var result = new StringBuilder();
        foreach (var segment in ExpressionTemplate.Parse(expression))
        {
            result.Append(segment.IsExpression
                ? ToTemplateString(engine, expression, Run(engine, expression, segment.Text))
                : segment.Text);
        }

        return JsonValue.Create(result.ToString());
    }

    private Jint.Engine CreateEngine(string expression, ExpressionScope scope)
    {
        var timeZone = ResolveTimeZone(expression, scope.Timezone);
        var engine = new Jint.Engine(options =>
        {
            options.TimeoutInterval(_options.Timeout);
            options.LimitMemory(_options.MemoryLimitBytes);
            options.LimitRecursion(_options.RecursionLimit);
            options.Host.StringCompilationAllowed = false;
            options.TimeZone = timeZone;
            options.TimeSystem = new FixedTimeSystem(timeZone, scope.Now);
            options.Culture = CultureInfo.InvariantCulture;
        });

        try
        {
            var parser = new JsonParser(engine);
            engine.SetValue("$json", parser.Parse(scope.Item.Json.ToJsonString()));
            engine.SetValue("$now", engine.Construct("Date", new JsNumber(scope.Now.ToUnixTimeMilliseconds())));
            engine.SetValue("$execution", parser.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["id"] = scope.ExecutionId.ToString(),
            })));

            // Великі колекції серіалізуються лише тоді, коли вираз на них посилається.
            if (expression.Contains("$input", StringComparison.Ordinal))
            {
                var items = parser.Parse(SerializeItems(scope.Input));
                var factory = engine.Evaluate(InputFactory);
                engine.SetValue("$input", engine.Invoke(factory, items, scope.ItemIndex));
            }

            if (expression.Contains("$node", StringComparison.Ordinal))
            {
                engine.SetValue("$node", parser.Parse(SerializeNodes(scope.Nodes, scope.ItemIndex)));
            }

            if (expression.Contains("$env", StringComparison.Ordinal))
            {
                engine.SetValue("$env", parser.Parse(JsonSerializer.Serialize(scope.Env)));
            }
        }
        catch (Exception ex) when (ex is JintException or JavaScriptException or JsonException)
        {
            throw new ExpressionException(expression, $"Не вдалося підготувати змінні виразу: {ex.Message}", ex);
        }

        return engine;
    }

    private static TimeZoneInfo ResolveTimeZone(string expression, string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ExpressionException(expression, $"Невідомий часовий пояс сценарію «{timezone}».", ex);
        }
    }

    private static JsValue Run(Jint.Engine engine, string expression, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ExpressionException(expression, "Порожній вираз {{ }}.");
        }

        try
        {
            // Дужки — щоб "{{ { a: 1 } }}" був об'єктом, а не блоком; перенос рядка — щоб "// коментар" не з'їв дужку.
            return engine.Evaluate("(" + body + "\n)");
        }
        catch (JavaScriptException ex)
        {
            throw new ExpressionException(expression, $"Помилка у виразі: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new ExpressionException(expression, "Вираз перевищив ліміт часу.", ex);
        }
        catch (MemoryLimitExceededException ex)
        {
            throw new ExpressionException(expression, "Вираз перевищив ліміт пам'яті.", ex);
        }
        catch (RecursionDepthOverflowException ex)
        {
            throw new ExpressionException(expression, "Вираз перевищив ліміт глибини рекурсії.", ex);
        }
        catch (Exception ex) when (ex is JintException or Acornima.SyntaxErrorException or Acornima.ParseErrorException)
        {
            throw new ExpressionException(expression, $"Помилка у виразі: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Хост Jint відмовляє у недоступних можливостях (напр. import() без завантажувача модулів).
            throw new ExpressionException(expression, $"Операція недоступна у виразі: {ex.Message}", ex);
        }
    }

    private static JsonNode? ToJsonNode(Jint.Engine engine, string expression, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value.IsPromise())
        {
            throw new ExpressionException(expression, "Вираз повернув Promise; асинхронні вирази не підтримуються.");
        }

        if (value is Jint.Native.Function.Function)
        {
            throw new ExpressionException(expression, "Вираз повернув функцію замість значення.");
        }

        try
        {
            var json = new JsJsonSerializer(engine).Serialize(value);
            return json.IsUndefined() ? null : JsonNode.Parse(json.AsString());
        }
        catch (Exception ex) when (ex is JavaScriptException or JintException)
        {
            throw new ExpressionException(expression, $"Результат виразу не перетворюється на JSON: {ex.Message}", ex);
        }
    }

    private static string ToTemplateString(Jint.Engine engine, string expression, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return "";
        }

        if (value.IsString() || value.IsNumber() || value.IsBoolean())
        {
            return TypeConverter.ToString(value);
        }

        if (value.IsDate())
        {
            try
            {
                return value.AsDate().ToDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }
            catch (JavaScriptException)
            {
                // Невалідна дата (new Date('не дата')) — так само, як її показує JS.
                return "Invalid Date";
            }
        }

        var node = ToJsonNode(engine, expression, value);
        return node?.ToJsonString() ?? "";
    }

    private static string SerializeItems(IReadOnlyList<Item> items)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendItem(builder, items[i]);
        }

        return builder.Append(']').ToString();
    }

    private static string SerializeNodes(IReadOnlyDictionary<string, IReadOnlyList<Item>> nodes, int itemIndex)
    {
        var result = new JsonObject();
        foreach (var (name, items) in nodes)
        {
            var item = itemIndex < items.Count ? items[itemIndex] : items.Count > 0 ? items[0] : null;
            var builder = new StringBuilder();
            if (item is null)
            {
                builder.Append("{\"json\":{}}");
            }
            else
            {
                AppendItem(builder, item);
            }

            result[name] = JsonNode.Parse(builder.ToString());
        }

        return result.ToJsonString();
    }

    private static void AppendItem(StringBuilder builder, Item item)
    {
        builder.Append("{\"json\":").Append(item.Json.ToJsonString());
        if (item.Binary is { Count: > 0 })
        {
            builder.Append(",\"binary\":").Append(JsonSerializer.Serialize(item.Binary, WorkflowJson.Options));
        }

        builder.Append('}');
    }
}
