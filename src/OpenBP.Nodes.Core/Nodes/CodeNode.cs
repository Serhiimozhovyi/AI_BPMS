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
/// <c>$json</c> (дані item'а), <c>$index</c> і <c>$node["Назва"].json</c> (вихід уже виконаного вузла: item з тим самим
/// індексом або перший, як у виразах); повертає об'єкт (новий <c>json</c>), масив об'єктів (кілька items)
/// або <c>null</c> (item відкидається). Бінарні вкладення item'а зберігаються.
/// </para>
/// <para>
/// <c>mode: allItems</c> — код виконується один раз зі змінною <c>items</c> (масив <c>json</c> усіх items)
/// і <c>$node["Назва"].json</c> (перший item; усі — <c>$node["Назва"].all</c>), повертає масив об'єктів; бінарні вкладення
/// не переносяться.
/// </para>
/// <para>
/// <c>$vars.key</c> — змінні інстансу (F6-09) в обох режимах.
/// <c>$node["Назва"]</c> вузла, який не виконувався, — помилка з його назвою. <c>Date</c> працює в часовому поясі
/// сценарію (<c>settings.timezone</c>), а не в UTC.
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
        var timeZone = ResolveTimeZone(ctx.Timezone);
        var nodes = ctx.NodeOutputs;
        switch (first.Mode)
        {
            case "eachItem":
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var p = i == 0 ? first : ctx.Params<CodeParameters>(i);
                    var result = Run(p.Code, timeZone, deadline.Token, ct, (engine, parser) =>
                    {
                        engine.SetValue("$json", parser.Parse(items[i].Json.ToJsonString()));
                        engine.SetValue("$index", i);
                        SetNodes(engine, parser, p.Code, nodes, i);
                        SetVars(engine, parser, p.Code, ctx.Vars);
                    });
                    output.AddRange(ToJsonObjects(result, allowSingle: true).Select(json => ItemCopy.WithJson(items[i], json)));
                }

                break;
            case "allItems":
                var all = Run(first.Code, timeZone, deadline.Token, ct, (engine, parser) =>
                {
                    var array = new JsonArray([.. items.Select(item => (JsonNode)item.Json.DeepClone())]);
                    engine.SetValue("items", parser.Parse(array.ToJsonString()));
                    SetNodes(engine, parser, first.Code, nodes, 0);
                    SetVars(engine, parser, first.Code, ctx.Vars);
                });
                output.AddRange(ToJsonObjects(all, allowSingle: false).Select(json => new Item { Json = json }));
                break;
            default:
                throw new CoreNodeException($"Невідомий режим «{first.Mode}»; очікується eachItem або allItems.");
        }

        return Task.FromResult<Item[][]>([[.. output]]);
    }

    /// <summary>
    /// <c>$node</c>: <c>{ "Назва": { json, all } }</c> у Proxy, який на назву невиконаного вузла кидає помилку з назвою
    /// (інакше користувач бачив би лише «Cannot read property 'json' of undefined»). Серіалізується, лише якщо код
    /// згадує <c>$node</c>.
    /// </summary>
    private static void SetNodes(Jint.Engine engine, JsonParser parser, string code, IReadOnlyDictionary<string, IReadOnlyList<Item>> nodes, int itemIndex)
    {
        if (!code.Contains("$node", StringComparison.Ordinal))
        {
            return;
        }

        var data = new JsonObject();
        foreach (var (name, items) in nodes)
        {
            var item = itemIndex < items.Count ? items[itemIndex] : items.Count > 0 ? items[0] : null;
            data[name] = new JsonObject
            {
                ["json"] = item?.Json.DeepClone() ?? new JsonObject(),
                ["all"] = new JsonArray([.. items.Select(i => (JsonNode)i.Json.DeepClone())]),
            };
        }

        var factory = engine.Evaluate(NodeProxyFactory);
        engine.SetValue("$node", engine.Invoke(factory, parser.Parse(data.ToJsonString())));
    }

    /// <summary><c>$vars</c> — змінні інстансу (F6-09), лише якщо код їх згадує.</summary>
    private static void SetVars(Jint.Engine engine, JsonParser parser, string code, IReadOnlyDictionary<string, string> vars)
    {
        if (code.Contains("$vars", StringComparison.Ordinal))
        {
            engine.SetValue("$vars", parser.Parse(System.Text.Json.JsonSerializer.Serialize(vars)));
        }
    }

    private const string NodeProxyFactory = """
        (function (nodes) {
          return new Proxy(nodes, {
            get(target, key) {
              if (typeof key !== 'string' || key in target || key === 'toJSON' || key === 'then') return target[key];
              const known = Object.keys(target);
              throw new Error('Вузол «' + key + '» ще не виконувався в цьому запуску або не існує' +
                (known.length ? '; доступні: ' + known.map(n => '«' + n + '»').join(', ') : '') + '.');
            },
            set() { return false; },
            deleteProperty() { return false; },
          });
        })
        """;

    private static TimeZoneInfo ResolveTimeZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new CoreNodeException($"Невідомий часовий пояс сценарію «{timezone}».", ex);
        }
    }

    private JsonNode? Run(string code, TimeZoneInfo timeZone, CancellationToken deadline, CancellationToken ct, Action<Jint.Engine, JsonParser> setup)
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
            options.TimeZone = timeZone;
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
