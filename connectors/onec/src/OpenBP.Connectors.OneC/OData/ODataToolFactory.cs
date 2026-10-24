using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using OpenBP.Connectors.OneC.Generated;

namespace OpenBP.Connectors.OneC.OData;

/// <summary>
/// Tools на кожен entity set OData: <c>list_&lt;set&gt;</c> (<c>$filter</c>, <c>$select</c>, <c>$expand</c>,
/// <c>$orderby</c>, <c>$top</c>, <c>$skip</c>; відповідь <c>value</c> → <c>{ "result": [...] }</c>), <c>get_&lt;set&gt;</c>
/// (за ключем, зокрема складеним), <c>create_&lt;set&gt;</c> (POST) і <c>update_&lt;set&gt;</c> (PATCH). <c>inputSchema</c>
/// — з типів CSDL (Edm.* → JSON Schema, складні типи й перелічення розгортаються). Посилання на довідник
/// (<c>&lt;Name&gt;_Key</c> з навігацією <c>&lt;Name&gt;</c> на entity set, для якого є <c>list_*</c>) отримують анотацію
/// <c>x-openbp-options</c> — у формі OpenBP значення обирається зі списку (docs/connectors.md §2).
/// </summary>
public static class ODataToolFactory
{
    /// <summary>Аргумент із тілом сутності для create/update.</summary>
    public const string BodyArgument = "body";

    /// <summary>Анотація властивості <c>inputSchema</c>: звідки форма OpenBP бере список значень (docs/connectors.md §2).</summary>
    public const string OptionsAnnotation = "x-openbp-options";

    /// <summary>Суфікс властивості-посилання 1С (<c>Контрагент_Key</c> ↔ навігація <c>Контрагент</c>).</summary>
    private const string KeySuffix = "_Key";

    /// <summary>Властивість з назвою елемента довідника 1С — підпис у списку.</summary>
    private const string LabelProperty = "Description";

    private const int MaxDepth = 4;

    /// <summary>Будує tools.</summary>
    /// <param name="model">Модель з <c>$metadata</c>.</param>
    /// <param name="options">Фільтр entity sets і особливості сервера.</param>
    /// <param name="rest">Клієнт сервісу (базова адреса й автентифікація конектора).</param>
    public static IReadOnlyList<RestTool> Create(CsdlModel model, ODataOptions options, Func<RestClient> rest)
    {
        var include = options.Include.Select(Pattern).ToList();
        var exclude = options.Exclude.Select(Pattern).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tools = new List<RestTool>();

        // Спершу назви tools усіх entity sets (у тому самому порядку, що й раніше): посилання на довідник має знати
        // назву list_* цільового set'а, навіть якщо той іде далі в $metadata.
        var plans = new List<SetPlan>();
        foreach (var set in model.EntitySets)
        {
            if ((include.Count > 0 && !include.Any(p => p.IsMatch(set.Name))) || exclude.Any(p => p.IsMatch(set.Name)))
            {
                continue;
            }

            if (model.FindType(set.EntityType) is not { } planType)
            {
                continue;
            }

            var slugName = ToolNaming.ToolName(set.Name) ?? "set";
            var hasKeys = model.KeysOf(planType).Count > 0;
            var list = ToolNaming.Unique($"list_{slugName}", names);
            var get = hasKeys ? ToolNaming.Unique($"get_{slugName}", names) : null;
            var create = ToolNaming.Unique($"create_{slugName}", names);
            var update = hasKeys ? ToolNaming.Unique($"update_{slugName}", names) : null;
            plans.Add(new SetPlan(set, planType, list, get, create, update));
        }

        foreach (var (set, type, listName, getName, createName, updateName) in plans)
        {
            var keys = model.KeysOf(type);
            var properties = model.PropertiesOf(type);
            var navigations = model.NavigationsOf(type).Select(n => n.Name).ToList();
            var entity = EntitySchema(model, type, properties, plans);
            var keyProperties = new JsonObject();
            foreach (var key in keys)
            {
                keyProperties[key.Name] = EdmSchema(model, key.Type, key.MaxLength, 0);
            }

            var keyNames = keys.Select(k => k.Name).ToArray();
            var binding = new ODataBinding(set.Name, keys, model.IsV4, options.FormatParameter);

            var select = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(properties.Select(p => p.Name)) },
                ["description"] = "Properties to return ($select); omit for all.",
            };
            var expand = navigations.Count == 0 ? null : new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Strings(navigations) },
                ["description"] = "Navigation properties to include inline ($expand).",
            };

            var listProperties = new JsonObject
            {
                ["filter"] = new JsonObject { ["type"] = "string", ["description"] = "OData $filter expression, e.g. Description eq 'Acme'." },
                ["select"] = select.DeepClone(),
                ["orderby"] = new JsonObject { ["type"] = "string", ["description"] = "OData $orderby, e.g. Date desc." },
                ["top"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Max number of entities ($top)." },
                ["skip"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Number of entities to skip ($skip)." },
            };
            if (expand is not null)
            {
                listProperties["expand"] = expand.DeepClone();
            }

            tools.Add(new ODataTool(
                Tool(listName, $"List {set.Name}",
                    $"Lists entities of the OData entity set {set.Name}. Returns {{ result: {Short(set.EntityType)}[] }}.",
                    Schema(listProperties, []), readOnly: true),
                ODataOperation.List, binding, rest));

            if (getName is not null)
            {
                var getProperties = (JsonObject)keyProperties.DeepClone();
                getProperties["select"] = select.DeepClone();
                if (expand is not null)
                {
                    getProperties["expand"] = expand.DeepClone();
                }

                tools.Add(new ODataTool(
                    Tool(getName, $"Get {set.Name}",
                        $"Returns one entity of {set.Name} by key ({string.Join(", ", keyNames)}).",
                        Schema(getProperties, keyNames), readOnly: true),
                    ODataOperation.Get, binding, rest));
            }

            tools.Add(new ODataTool(
                Tool(createName, $"Create {set.Name}",
                    $"Creates an entity in {set.Name} (POST). Returns the created entity.",
                    Schema(new JsonObject { [BodyArgument] = entity.DeepClone() }, [BodyArgument]), readOnly: false),
                ODataOperation.Create, binding, rest));

            if (updateName is not null)
            {
                var updateProperties = (JsonObject)keyProperties.DeepClone();
                var patch = (JsonObject)entity.DeepClone();
                patch["description"] = "Properties to change (PATCH); omitted properties stay as they are.";
                updateProperties[BodyArgument] = patch;
                tools.Add(new ODataTool(
                    Tool(updateName, $"Update {set.Name}",
                        $"Updates an entity of {set.Name} by key ({string.Join(", ", keyNames)}) with PATCH.",
                        Schema(updateProperties, [.. keyNames, BodyArgument]), readOnly: false),
                    ODataOperation.Update, binding, rest));
            }
        }

        return tools;
    }

    /// <summary>JSON Schema сутності: об'єкт з усіма структурними властивостями; посилання на довідники — з анотацією списку.</summary>
    private static JsonObject EntitySchema(CsdlModel model, CsdlType owner, IReadOnlyList<CsdlProperty> properties, IReadOnlyList<SetPlan> plans)
    {
        var props = new JsonObject();
        foreach (var property in properties)
        {
            var schema = EdmSchema(model, property.Type, property.MaxLength, 1);
            if (ReferenceOptions(model, owner, property, plans) is { } options)
            {
                schema[OptionsAnnotation] = options;
            }

            props[property.Name] = schema;
        }

        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    /// <summary>
    /// <c>x-openbp-options</c> для <c>&lt;Name&gt;_Key</c>: навігація <c>&lt;Name&gt;</c> веде на один об'єкт типу, для якого є
    /// entity set з <c>list_*</c> і простим ключем; значення — ключ, підпис — <c>Description</c> (якщо є).
    /// </summary>
    internal static JsonObject? ReferenceOptions(CsdlModel model, CsdlType owner, CsdlProperty property, IReadOnlyList<SetPlan> plans)
    {
        if (!property.Name.EndsWith(KeySuffix, StringComparison.Ordinal) || property.Name.Length == KeySuffix.Length)
        {
            return null;
        }

        var navigationName = property.Name[..^KeySuffix.Length];
        var navigation = model.NavigationsOf(owner).FirstOrDefault(n => n.Name == navigationName);
        if (navigation?.Type is not { } targetName || targetName.StartsWith("Collection(", StringComparison.Ordinal))
        {
            return null;
        }

        var target = model.FindType(targetName);
        var plan = target is null ? null : plans.FirstOrDefault(p => ReferenceEquals(p.Type, target));
        if (plan is null || model.KeysOf(plan.Type) is not [var key])
        {
            return null;
        }

        var label = model.PropertiesOf(plan.Type).Any(p => p.Name == LabelProperty) ? LabelProperty : key.Name;
        return new JsonObject
        {
            ["tool"] = plan.List,
            ["arguments"] = new JsonObject { ["select"] = Strings(new[] { key.Name, label }.Distinct()) },
            ["itemsPath"] = "result",
            ["valuePath"] = key.Name,
            ["labelPath"] = label,
        };
    }

    /// <summary>Тип CSDL → JSON Schema.</summary>
    internal static JsonObject EdmSchema(CsdlModel model, string type, int? maxLength, int depth)
    {
        if (type.StartsWith("Collection(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            return new JsonObject { ["type"] = "array", ["items"] = EdmSchema(model, type[11..^1], maxLength, depth + 1) };
        }

        var schema = type switch
        {
            "Edm.String" => new JsonObject { ["type"] = "string" },
            "Edm.Boolean" => new JsonObject { ["type"] = "boolean" },
            "Edm.Byte" or "Edm.SByte" or "Edm.Int16" or "Edm.Int32" or "Edm.Int64" => new JsonObject { ["type"] = "integer" },
            "Edm.Decimal" or "Edm.Double" or "Edm.Single" => new JsonObject { ["type"] = "number" },
            "Edm.Guid" => new JsonObject { ["type"] = "string", ["format"] = "uuid" },
            "Edm.DateTimeOffset" or "Edm.DateTime" => new JsonObject { ["type"] = "string", ["format"] = "date-time" },
            "Edm.Date" => new JsonObject { ["type"] = "string", ["format"] = "date" },
            "Edm.TimeOfDay" or "Edm.Time" => new JsonObject { ["type"] = "string", ["format"] = "time" },
            "Edm.Duration" => new JsonObject { ["type"] = "string", ["format"] = "duration" },
            "Edm.Binary" or "Edm.Stream" => new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" },
            _ => null,
        };
        if (schema is not null)
        {
            if (maxLength is { } max && type == "Edm.String")
            {
                schema["maxLength"] = max;
            }

            return schema;
        }

        if (model.FindEnum(type) is { } members)
        {
            return new JsonObject { ["type"] = "string", ["enum"] = Strings(members) };
        }

        if (model.FindType(type) is { } complex && depth < MaxDepth)
        {
            var props = new JsonObject();
            foreach (var property in model.PropertiesOf(complex))
            {
                props[property.Name] = EdmSchema(model, property.Type, property.MaxLength, depth + 1);
            }

            return new JsonObject { ["type"] = "object", ["properties"] = props };
        }

        // Невідомий тип (напр. геопросторовий) чи надто глибока вкладеність — будь-яке значення.
        return new JsonObject { ["description"] = type };
    }

    /// <summary>Entity set з назвами його tools (<c>null</c> — tool не створюється: немає ключа).</summary>
    internal sealed record SetPlan(CsdlEntitySet Set, CsdlType Type, string List, string? Get, string Create, string? Update);

    private static JsonObject Schema(JsonObject properties, string[] required)
    {
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Length > 0)
        {
            schema["required"] = Strings(required);
        }

        return schema;
    }

    private static Tool Tool(string name, string title, string description, JsonObject schema, bool readOnly) => new()
    {
        Name = name,
        Title = title,
        Description = description,
        InputSchema = JsonSerializer.SerializeToElement(schema),
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = readOnly,
            DestructiveHint = readOnly ? null : false,
            IdempotentHint = readOnly,
            OpenWorldHint = true,
        },
    };

    private static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(v => (JsonNode)v)];

    private static string Short(string qualified) => qualified[(qualified.LastIndexOf('.') + 1)..];

    private static Regex Pattern(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
