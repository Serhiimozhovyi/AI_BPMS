using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.code</c> — користувацький JavaScript у пісочниці Jint (CLAUDE.md, правило 5; docs/architecture.md §5):
/// без CLR-interop, без <c>require</c>/модулів, без мережі й файлів, без <c>eval</c>/<c>new Function</c> з рядка;
/// ліміти — <see cref="DefaultTimeout"/> на весь запуск вузла, 16 МБ пам'яті, рекурсія 64.
/// <para>
/// <c>mode: eachItem</c> (за замовчуванням) — код виконується для кожного item'а окремо зі змінними
/// <c>$json</c> (дані item'а) і <c>$index</c>; повертає об'єкт (новий <c>json</c>), масив об'єктів (кілька items)
/// або <c>null</c> (item відкидається). Бінарні вкладення item'а зберігаються.
/// </para>
/// <para>
/// <c>mode: allItems</c> — код виконується один раз зі змінною <c>items</c> (масив <c>json</c> усіх items)
/// і повертає масив об'єктів; бінарні вкладення не переносяться.
/// </para>
/// </summary>
public sealed class CodeNode : INode
{
    /// <summary>Ліміт часу на весь запуск вузла.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Ліміт пам'яті одного виконання коду, у байтах.</summary>
    public const long MemoryLimitBytes = 16L * 1024 * 1024;

    /// <summary>Максимальна глибина рекурсії JS-функцій.</summary>
    public const int RecursionLimit = 64;

    private readonly TimeSpan _timeout;

    /// <summary>Створює вузол.</summary>
    /// <param name="timeout">Ліміт часу на запуск; <c>null</c> — <see cref="DefaultTimeout"/>. Більший за типовий не допускається.</param>
    public CodeNode(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > DefaultTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"Ліміт має бути в межах (0; {DefaultTimeout.TotalSeconds} с].");
        }
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: "core.code",
        Version: 1,
        DisplayName: "Код (JavaScript)",
        Group: NodeGroup.Transform,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("code"),
        Description: "Виконує JavaScript у пісочниці (до 5 с, без доступу до файлів і мережі).");

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var items = ctx.Items();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);

        var output = new List<Item>();
        var first = ctx.Params<CodeParameters>(0);
        switch (first.Mode)
        {
            case "eachItem":
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = i == 0 ? first : ctx.Params<CodeParameters>(i);
                    var result = Run(p.Code, deadline.Token, ct, (engine, parser) =>
                    {
                        engine.SetValue("$json", parser.Parse(items[i].Json.ToJsonString()));
                        engine.SetValue("$index", i);
                    });
                    output.AddRange(ToJsonObjects(result, allowSingle: true).Select(json => ItemCopy.WithJson(items[i], json)));
                }

                break;
            case "allItems":
                var all = Run(first.Code, deadline.Token, ct, (engine, parser) =>
                {
                    var array = new JsonArray([.. items.Select(item => (JsonNode)item.Json.DeepClone())]);
                    engine.SetValue("items", parser.Parse(array.ToJsonString()));
                });
                output.AddRange(ToJsonObjects(all, allowSingle: false).Select(json => new Item { Json = json }));
                break;
            default:
                throw new CoreNodeException($"Невідомий режим «{first.Mode}»; очікується eachItem або allItems.");
        }

        return Task.FromResult<Item[][]>([[.. output]]);
    }

    private JsonNode? Run(string code, CancellationToken deadline, CancellationToken ct, Action<Jint.Engine, JsonParser> setup)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new CoreNodeException("Код порожній.");
        }

        var engine = new Jint.Engine(options =>
        {
            options.TimeoutInterval(_timeout);
            options.CancellationToken(deadline);
            options.LimitMemory(MemoryLimitBytes);
            options.LimitRecursion(RecursionLimit);
            options.Host.StringCompilationAllowed = false;
            options.Culture = CultureInfo.InvariantCulture;
            options.TimeZone = TimeZoneInfo.Utc;
        });

        try
        {
            setup(engine, new JsonParser(engine));

            // Тіло функції: користувач пише return; перенос рядка — щоб "// коментар" не з'їв дужку.
            var value = engine.Evaluate("(function () {\n" + code + "\n})()");
            return ToJsonNode(engine, value);
        }
        catch (ExecutionCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is ExecutionCanceledException or TimeoutException)
        {
            throw new CoreNodeException($"Код перевищив ліміт часу {_timeout.TotalSeconds:0.###} с.", ex);
        }
        catch (MemoryLimitExceededException ex)
        {
            throw new CoreNodeException("Код перевищив ліміт пам'яті.", ex);
        }
        catch (RecursionDepthOverflowException ex)
        {
            throw new CoreNodeException("Код перевищив ліміт глибини рекурсії.", ex);
        }
        catch (JavaScriptException ex)
        {
            throw new CoreNodeException($"Помилка в коді: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is JintException or Acornima.SyntaxErrorException or Acornima.ParseErrorException)
        {
            throw new CoreNodeException($"Помилка в коді: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Хост Jint відмовляє в недоступних можливостях (import() без завантажувача модулів тощо).
            throw new CoreNodeException($"Операція недоступна в коді: {ex.Message}", ex);
        }
    }

    private static JsonNode? ToJsonNode(Jint.Engine engine, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        if (value.IsPromise() || value is Jint.Native.Function.Function)
        {
            throw new CoreNodeException("Код має повертати об'єкт або масив об'єктів, а не функцію чи Promise.");
        }

        var json = new JsJsonSerializer(engine).Serialize(value);
        return json.IsUndefined() ? null : JsonNode.Parse(json.AsString());
    }

    private static IEnumerable<JsonObject> ToJsonObjects(JsonNode? result, bool allowSingle)
    {
        switch (result)
        {
            case null when allowSingle:
                return [];
            case JsonObject obj when allowSingle:
                return [obj];
            case JsonArray array:
                return array.Select((element, index) => element as JsonObject
                    ?? throw new CoreNodeException($"Елемент {index} результату коду має бути об'єктом, а отримано {Kind(element)}."))
                    .ToList();
            default:
                throw new CoreNodeException(allowSingle
                    ? $"Код має повертати об'єкт, масив об'єктів або null, а повернув {Kind(result)}."
                    : $"У режимі allItems код має повертати масив об'єктів, а повернув {Kind(result)}.");
        }
    }

    private static string Kind(JsonNode? node) => node is null ? "null" : node.GetValueKind() switch
    {
        JsonValueKind.Array => "масив",
        JsonValueKind.Object => "об'єкт",
        JsonValueKind.String => "рядок",
        JsonValueKind.Number => "число",
        JsonValueKind.True or JsonValueKind.False => "булеве",
        _ => "null",
    };

    private sealed class CodeParameters
    {
        public string Code { get; set; } = "";

        public string Mode { get; set; } = "eachItem";
    }
}
