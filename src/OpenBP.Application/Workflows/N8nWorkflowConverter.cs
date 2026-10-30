using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Зауваження перетворення n8n → AI_BPMS.</summary>
/// <param name="Level"><c>replace</c> — вузол не перенесено, на його місці заглушка, яку треба замінити;
/// <c>check</c> — перенесено, але поведінку варто перевірити.</param>
/// <param name="Node">Назва вузла (однакова в n8n і AI_BPMS) або <c>null</c> для сценарію загалом.</param>
/// <param name="Message">Що сталося і що зробити.</param>
public sealed record N8nConversionNote(string Level, string? Node, string Message);

/// <summary>Результат <c>POST /workflows/convert/n8n</c>: тіло для <c>POST /workflows/import</c> і зауваження.</summary>
/// <param name="Definition">Сценарій у форматі AI_BPMS.</param>
/// <param name="Manifest">Конектори й credentials, які треба вибрати при імпорті (з назвами credentials n8n).</param>
/// <param name="Notes">Зауваження: спершу <c>replace</c>, потім <c>check</c>.</param>
public sealed record N8nConversion(WorkflowDefinition Definition, WorkflowManifest Manifest, IReadOnlyList<N8nConversionNote> Notes);

/// <summary>
/// Перетворює експорт сценарію n8n (JSON з «Download» чи буфера обміну) у формат AI_BPMS (docs/workflow-format.md
/// §«Імпорт з n8n»). Код n8n не використовується — лише відомий формат його JSON. Вузли без відповідника стають
/// заглушками <c>core.code</c> з тією самою назвою і зв'язками, тож граф зберігається, а список
/// <see cref="N8nConversionNote"/> каже, що замінити.
/// </summary>
public static partial class N8nWorkflowConverter
{
    /// <summary>Рівень зауваження: вузол не перенесено.</summary>
    public const string Replace = "replace";

    /// <summary>Рівень зауваження: перенесено, перевірте.</summary>
    public const string Check = "check";

    private const string TelegramTool = "send_message";
    private const string HttpReference = "n8n:http";
    private const int MaxNotesLength = 4000;

    /// <summary>
    /// Чи схожий JSON на експорт n8n: <c>nodes</c> — масив, <c>connections</c> — об'єкт за назвами вузлів
    /// (в AI_BPMS — масив ребер), або типи вузлів n8n (<c>n8n-nodes-base.*</c>, <c>@n8n/*</c>).
    /// </summary>
    /// <param name="json">Вміст файлу.</param>
    public static bool IsN8n(JsonNode? json) =>
        json is JsonObject obj && obj["nodes"] is JsonArray nodes
        && (obj["connections"] is JsonObject
            || nodes.OfType<JsonObject>().Any(n => Str(n["type"]) is { } t && (t.StartsWith("n8n-nodes-", StringComparison.Ordinal) || t.StartsWith("@n8n/", StringComparison.Ordinal))));

    /// <summary>Перетворює сценарій n8n.</summary>
    /// <param name="workflow">Експорт n8n.</param>
    /// <exception cref="ArgumentException">Це не сценарій n8n.</exception>
    public static N8nConversion Convert(JsonObject workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (!IsN8n(workflow))
        {
            throw new ArgumentException("Це не експорт сценарію n8n: потрібні масив nodes і об'єкт connections.", nameof(workflow));
        }

        return new Conversion(workflow).Run();
    }

    /// <summary>Вираз n8n (<c>={{ … }}</c>) → вираз AI_BPMS; рядок без <c>=</c> — літерал.</summary>
    internal static string Expression(string value, Action<string>? note = null, string? timezone = null)
    {
        if (!value.StartsWith('='))
        {
            return value;
        }

        var text = NodeAccessRegex().Replace(value[1..], m => $"$node[\"{m.Groups["name"].Value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"].json");
        // «Send and Wait» у n8n віддає { data: { approved } }, get_approval — { approved } (без відповіді поля немає).
        text = text.Replace("$json.data.approved", "$json.approved", StringComparison.Ordinal);
        var rewritten = RewriteLuxon(text, "$now", timezone, note);
        if (rewritten != text)
        {
            note?.Invoke("Дату Luxon ($now.setZone(…).toISODate()/toFormat(…), $today) замінено еквівалентом на Date: в AI_BPMS $now — звичайний Date, а пояс — пояс сценарію.");
            text = rewritten;
        }

        if (note is not null)
        {
            if (AllItemsRegex().IsMatch(text))
            {
                note("Вираз з $('…').all() не перенесено: у виразах AI_BPMS є лише $node[\"Назва\"].json (item з тим самим індексом).");
            }

            if (LuxonRegex().IsMatch(text))
            {
                note("Вираз використовує Luxon ($now.setZone/toISODate/toFormat/plus/minus, DateTime): в AI_BPMS $now — звичайний Date, перепишіть вираз.");
            }
        }

        return text;
    }

    /// <summary>
    /// Типові виклики Luxon → еквіваленти на <c>Date</c> (пояс — пояс сценарію): <c>.toISODate()</c>, <c>.toISO()</c>,
    /// <c>.toMillis()</c>, <c>.toJSDate()</c>, <c>.toFormat('yyyy-MM-dd HH:mm')</c> (токени yyyy, yy, MM, M, dd, d, HH, H, mm,
    /// ss) на <c>$now</c>, <c>$now.setZone('…')</c> і <c>$today</c>. Решта (<c>plus</c>, <c>startOf</c>, назви місяців) лишається.
    /// </summary>
    /// <param name="text">Вираз чи код.</param>
    /// <param name="now">Як у цьому місці отримати «зараз»: <c>$now</c> у виразі, <c>new Date()</c> у коді.</param>
    /// <param name="timezone">Пояс сценарію.</param>
    /// <param name="note">Зауваження.</param>
    internal static string RewriteLuxon(string text, string now, string? timezone, Action<string>? note) =>
        LuxonCallRegex().Replace(text, m =>
        {
            var zone = m.Groups["zone"].Success ? m.Groups["zone"].Value : null;
            var baseExpression = m.Groups["base"].Value.StartsWith("$today", StringComparison.Ordinal)
                ? $"((d) => (d.setHours(0, 0, 0, 0), d))(new Date({now}))"
                : now;
            string? replacement = m.Groups["method"].Value switch
            {
                "toISO" => $"{baseExpression}.toISOString()",
                "toMillis" => $"{baseExpression}.getTime()",
                "toJSDate" => baseExpression,
                "toISODate" => FormatDate(baseExpression, "yyyy-MM-dd"),
                _ => m.Groups["fmt"].Success ? FormatDate(baseExpression, m.Groups["fmt"].Value) : null,
            };
            if (replacement is not null && zone is not null && timezone is not null && !zone.Equals(timezone, StringComparison.OrdinalIgnoreCase))
            {
                note?.Invoke($"Пояс «{zone}» у виразі відрізняється від пояса сценарію «{timezone}»: AI_BPMS рахує дату в поясі сценарію — змініть його в налаштуваннях сценарію.");
            }

            return replacement ?? m.Value;
        });

    /// <summary>Формат Luxon → конкатенація з геттерів <c>Date</c>; <c>null</c>, якщо в форматі є токени, яких не підтримано.</summary>
    private static string? FormatDate(string date, string format)
    {
        var parts = new List<string>();
        var literal = new StringBuilder();
        void Flush()
        {
            if (literal.Length > 0)
            {
                parts.Add("'" + literal.ToString().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'");
                literal.Clear();
            }
        }

        for (var i = 0; i < format.Length;)
        {
            var c = format[i];
            if (!char.IsLetter(c))
            {
                literal.Append(c);
                i++;
                continue;
            }

            var run = 1;
            while (i + run < format.Length && format[i + run] == c)
            {
                run++;
            }

            var token = new string(c, run);
            string? part = token switch
            {
                "yyyy" => "d.getFullYear()",
                "yy" => "p(d.getFullYear() % 100)",
                "MM" => "p(d.getMonth() + 1)",
                "M" => "(d.getMonth() + 1)",
                "dd" => "p(d.getDate())",
                "d" => "d.getDate()",
                "HH" => "p(d.getHours())",
                "H" => "d.getHours()",
                "mm" => "p(d.getMinutes())",
                "ss" => "p(d.getSeconds())",
                _ => null,
            };
            if (part is null)
            {
                return null;
            }

            Flush();
            parts.Add(part);
            i += run;
        }

        Flush();
        return $"((d, p) => {string.Join(" + ", parts.Count == 0 ? ["''"] : parts)})({date}, (n) => String(n).padStart(2, '0'))";
    }

    /// <summary>Код n8n у <c>core.code</c>: емуляція <c>$input</c>, <c>$('Назва')</c>, <c>items</c> і результату <c>[{ json }]</c>.</summary>
    internal static string CodeShim(string code, bool eachItem)
    {
        var sb = new StringBuilder();
        sb.Append("// Перенесено з n8n: $input, $('Назва'), items і результат [{ json }] емулюються нижче; решту API n8n перевірте.\n");
        if (eachItem)
        {
            sb.Append("const $input = { item: { json: $json }, first: () => ({ json: $json }), last: () => ({ json: $json }), all: () => [{ json: $json }] };\n");
        }
        else
        {
            sb.Append("const $input = { all: () => items.map((json) => ({ json })), first: () => ({ json: items[0] }), last: () => ({ json: items[items.length - 1] }) };\n");
        }

        sb.Append("const $ = (name) => ({ get item() { return { json: $node[name].json }; }, first: () => ({ json: $node[name].all[0] }), ")
            .Append("last: () => ({ json: $node[name].all[$node[name].all.length - 1] }), all: () => $node[name].all.map((json) => ({ json })) });\n");
        sb.Append("const __unwrap = (r) => (r && typeof r === 'object' && !Array.isArray(r) && r.json !== undefined ? r.json : r);\n");
        if (eachItem)
        {
            sb.Append("const __result = (() => {\n").Append(code).Append("\n})();\n");
            sb.Append("return Array.isArray(__result) ? __result.map(__unwrap) : __unwrap(__result);\n");
        }
        else
        {
            sb.Append("const __result = ((items) => (() => {\n").Append(code).Append("\n})())(items.map((json) => ({ json })));\n");
            sb.Append("return (Array.isArray(__result) ? __result : [__result]).map(__unwrap);\n");
        }

        return sb.ToString();
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Bool(JsonNode? node, bool fallback = false) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v
            ? v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<double>(out var d) ? (int)d
            : v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null
            : null;

    /// <summary>Значення ресурсного локатора n8n (<c>{ __rl, value, cachedResultName }</c>) або рядок.</summary>
    private static string? Locator(JsonNode? node, bool preferName = false) =>
        node is JsonObject rl
            ? (preferName ? Str(rl["cachedResultName"]) ?? Str(rl["value"]) : Str(rl["value"]) ?? Str(rl["cachedResultName"]))
            : Str(node);

    [GeneratedRegex("""\$\(\s*(?<q>['"`])(?<name>.+?)\k<q>\s*\)\s*\.\s*(?:item|first\(\s*\)|last\(\s*\))\s*\.\s*json""")]
    private static partial Regex NodeAccessRegex();

    [GeneratedRegex("""\$\(\s*['"`].+?['"`]\s*\)\s*\.\s*all\(""")]
    private static partial Regex AllItemsRegex();

    [GeneratedRegex("""\$now\s*\.\s*(?:setZone|toISO|toFormat|plus|minus|startOf|endOf|toMillis)|\$today|\bDateTime\.""")]
    private static partial Regex LuxonRegex();

    [GeneratedRegex("""(?<base>\$now(?:\s*\.\s*setZone\(\s*(?<q1>['"])(?<zone>[^'"]+)\k<q1>\s*\))?|\$today)\s*\.\s*(?<method>toISODate|toISO|toFormat|toMillis|toJSDate)\(\s*(?:(?<q2>['"])(?<fmt>[^'"]*)\k<q2>)?\s*\)""", RegexOptions.CultureInvariant)]
    private static partial Regex LuxonCallRegex();

    [GeneratedRegex("[^A-Za-z0-9._~/-]+")]
    private static partial Regex PathCharsRegex();

    /// <summary>Вузол AI_BPMS, у який перетворено вузол n8n.</summary>
    /// <summary>
    /// Вузол AI_BPMS, у який перетворено вузол n8n. <paramref name="Outputs"/> — виходи n8n; <paramref name="Prefix"/> і
    /// <paramref name="Suffix"/> — службові вузли до й після (Execute Once, розгортання відповіді HTTP, очікування
    /// відповіді погодження): вхідні зв'язки йдуть у перший, вихідні — з останнього.
    /// </summary>
    private sealed record Mapped(string Type, int Version, JsonObject Parameters, int Outputs, ExtraNode? Prefix = null, ExtraNode? Suffix = null, int? TimeoutSec = null);

    /// <summary>Службовий вузол поруч з основним.</summary>
    private sealed record ExtraNode(string Name, string Type, int Version, JsonObject Parameters, int Outputs, bool MirrorTimeout = false);

    /// <summary>Вузол n8n у перетвореному сценарії: <paramref name="Head"/> приймає зв'язки, <paramref name="Tail"/> — віддає.</summary>
    private sealed record NodeRef(string Id, Mapped Node, string? OnError, int? N8nErrorOutput, string Head, string Tail);

    private sealed class Conversion(JsonObject source)
    {
        private readonly List<N8nConversionNote> _notes = [];
        private readonly Dictionary<string, ManifestConnector> _connectors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ManifestCredential> _credentials = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedSet<string>> _tables = new(StringComparer.Ordinal);

        public N8nConversion Run()
        {
            var nodes = new JsonArray();
            var ids = new Dictionary<string, NodeRef>(StringComparer.Ordinal);
            var connections = source["connections"] as JsonObject ?? [];
            var skippedSticky = 0;
            var index = 0;
            var usedNames = new HashSet<string>((source["nodes"] as JsonArray ?? []).OfType<JsonObject>().Select(n => Str(n["name"]) ?? ""), StringComparer.Ordinal);
            foreach (var n8n in (source["nodes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var type = Str(n8n["type"]) ?? "";
                var name = Str(n8n["name"]);
                if (type == "n8n-nodes-base.stickyNote")
                {
                    skippedSticky++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) || ids.ContainsKey(name))
                {
                    _notes.Add(new(Check, name, "Вузол без назви або з повторною назвою пропущено."));
                    continue;
                }

                var id = $"n_{++index}";
                var mapped = MapNode(n8n, type, name, out var reason);
                var node = new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["position"] = Position(n8n["position"]),
                };
                var onError = Str(n8n["onError"]) ?? (Bool(n8n["continueOnFail"]) ? "continueRegularOutput" : null);
                if (mapped is null)
                {
                    mapped = Placeholder(type, name, reason!);
                    node["notes"] = Truncate($"n8n: {type}\n{reason}\nПараметри n8n:\n{n8n["parameters"]?.ToJsonString(Indented) ?? "{}"}");
                }
                else if (Str(n8n["notes"]) is { Length: > 0 } nodeNotes)
                {
                    node["notes"] = Truncate(nodeNotes);
                }

                node["type"] = mapped.Type;
                node["typeVersion"] = mapped.Version;
                node["parameters"] = mapped.Parameters;
                if (mapped.TimeoutSec is { } nodeTimeout)
                {
                    node["timeoutSec"] = nodeTimeout;
                }

                if (onError is "continueErrorOutput" or "continueRegularOutput")
                {
                    node["continueOnFail"] = true;
                }

                if (Bool(n8n["retryOnFail"]))
                {
                    node["retry"] = new JsonObject
                    {
                        ["maxTries"] = Math.Clamp(Int(n8n["maxTries"]) ?? 3, 1, 10),
                        ["waitMs"] = Math.Clamp(Int(n8n["waitBetweenTries"]) ?? 1000, 0, 60_000),
                    };
                }

                if (Bool(n8n["disabled"]))
                {
                    node["disabled"] = true;
                    _notes.Add(new(Check, name, "У n8n вузол вимкнено; в AI_BPMS вимкнення діє лише для тригерів — видаліть вузол, якщо він не потрібен."));
                }

                var prefix = mapped.Prefix;
                if (Bool(n8n["executeOnce"]))
                {
                    prefix ??= new ExtraNode(
                        $"{name}: лише перший item", "core.code", 1,
                        new JsonObject { ["code"] = "return items.slice(0, 1);", ["mode"] = "allItems" }, 1);
                    _notes.Add(new(Check, name, $"«Execute Once» перенесено вузлом core.code «{prefix.Name}» перед цим вузлом: далі йде лише перший item."));
                }

                if (Bool(n8n["alwaysOutputData"]))
                {
                    _notes.Add(new(Check, name, "«Always Output Data» не переноситься: без результатів вузол не віддасть порожній item."));
                }

                var head = id;
                if (prefix is not null)
                {
                    head = AddExtra(nodes, prefix, n8n, -1, ++index, usedNames);
                }

                var tail = id;
                nodes.Add(node);
                if (mapped.Suffix is { } suffix)
                {
                    tail = AddExtra(nodes, suffix, n8n, 1, ++index, usedNames);
                }

                // У n8n вихід помилки (continueErrorOutput) — останній з використаних.
                int? errorOutput = onError == "continueErrorOutput" && connections[name]?["main"] is JsonArray outs ? outs.Count - 1 : null;
                ids[name] = new NodeRef(id, mapped, onError, errorOutput, head, tail);
            }

            if (skippedSticky > 0)
            {
                _notes.Add(new(Check, null, $"Нотатки на полотні n8n ({skippedSticky}) не переносяться."));
            }

            var edges = Edges(connections, ids);
            var pinData = PinData(ids);
            foreach (var (table, columns) in _tables)
            {
                _notes.Add(new(Check, null, columns.Count > 0
                    ? $"Створіть таблицю даних «{table}» з колонками: {string.Join(", ", columns)} (Налаштування → «Таблиці даних»)."
                    : $"Створіть таблицю даних «{table}» (Налаштування → «Таблиці даних»)."));
            }

            if (Str(source["settings"]?["errorWorkflow"]) is { Length: > 0 })
            {
                _notes.Add(new(Check, null, "Сценарій помилок n8n не переноситься — виберіть його в «Налаштуваннях» сценарію."));
            }

            var timeout = Int(source["settings"]?["executionTimeout"]) is { } t and > 0 ? Math.Min(t, 3600) : 0;
            var document = new JsonObject
            {
                ["formatVersion"] = 1,
                ["name"] = Str(source["name"]) is { Length: > 0 } wfName ? wfName : "Імпорт з n8n",
                ["settings"] = new JsonObject
                {
                    ["timezone"] = Str(source["settings"]?["timezone"]) is { Length: > 0 } tz && tz != "DEFAULT" ? tz : "Europe/Kyiv",
                    ["executionTimeoutSec"] = timeout,
                    ["errorWorkflowId"] = null,
                    ["saveSuccessfulData"] = true,
                },
                ["nodes"] = nodes,
                ["connections"] = edges,
                ["pinData"] = pinData,
            };

            var definition = document.Deserialize<WorkflowDefinition>(WorkflowJson.Options)!;
            var manifest = new WorkflowManifest([.. _connectors.Values], [.. _credentials.Values], []);
            var notes = _notes.OrderBy(n => n.Level == Replace ? 0 : 1).ToList();
            return new N8nConversion(definition, manifest, notes);
        }

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static string Truncate(string text) => text.Length <= MaxNotesLength ? text : text[..MaxNotesLength] + "…";

        /// <summary>Додає службовий вузол над (<paramref name="side"/> −1) чи під (+1) основним; повертає його id.</summary>
        private static string AddExtra(JsonArray nodes, ExtraNode extra, JsonObject n8n, int side, int index, HashSet<string> usedNames)
        {
            var name = extra.Name;
            for (var n = 2; !usedNames.Add(name); n++)
            {
                name = $"{extra.Name} ({n})";
            }

            var position = Position(n8n["position"]);
            var id = $"n_{index}";
            nodes.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["type"] = extra.Type,
                ["typeVersion"] = extra.Version,
                ["position"] = new JsonArray(Int(position[0]) ?? 0, (Int(position[1]) ?? 0) + (side * 110)),
                ["parameters"] = extra.Parameters,
            });
            return id;
        }

        private static JsonArray Position(JsonNode? position) =>
            position is JsonArray { Count: >= 2 } p ? [Int(p[0]) ?? 0, Int(p[1]) ?? 0] : [0, 0];

        private JsonArray Edges(JsonObject connections, Dictionary<string, NodeRef> ids)
        {
            var edges = new List<(string From, int Output, string To, int Input)>();
            var otherTypes = new SortedSet<string>(StringComparer.Ordinal);
            // Службові вузли до й після основного (Execute Once, очікування відповіді, тіло відповіді HTTP).
            foreach (var reference in ids.Values)
            {
                if (reference.Head != reference.Id)
                {
                    edges.Add((reference.Head, 0, reference.Id, 0));
                }

                if (reference.Tail != reference.Id)
                {
                    edges.Add((reference.Id, 0, reference.Tail, 0));
                }
            }

            foreach (var (fromName, byType) in connections)
            {
                if (byType is not JsonObject types || !ids.TryGetValue(fromName, out var from))
                {
                    continue;
                }

                foreach (var (connectionType, outputs) in types)
                {
                    if (connectionType != "main")
                    {
                        otherTypes.Add(connectionType);
                        continue;
                    }

                    var list = (outputs as JsonArray ?? []).ToList();
                    for (var k = 0; k < list.Count; k++)
                    {
                        int output;
                        var source = from.Tail;
                        if (from.N8nErrorOutput == k)
                        {
                            output = from.Node.Outputs;
                            source = from.Id;
                        }
                        else if (k < from.Node.Outputs)
                        {
                            output = k;
                        }
                        else
                        {
                            if (list[k] is JsonArray { Count: > 0 })
                            {
                                _notes.Add(new(Replace, fromName, $"Зв'язки з виходу {k + 1} не перенесено: у заглушки один вихід."));
                            }

                            continue;
                        }

                        foreach (var target in (list[k] as JsonArray ?? []).OfType<JsonObject>())
                        {
                            if (Str(target["node"]) is not { } toName || !ids.TryGetValue(toName, out var to))
                            {
                                continue;
                            }

                            var input = to.Node.Type == "core.merge" && to.Head == to.Id ? Math.Clamp(Int(target["index"]) ?? 0, 0, 7) : 0;
                            edges.Add((source, output, to.Head, input));
                            // «Continue (regular output)»: помилка йде тим самим шляхом, що й успіх.
                            if (from.OnError == "continueRegularOutput" && k == 0)
                            {
                                edges.Add((from.Id, from.Node.Outputs, to.Head, input));
                            }

                            // Очікування відповіді: таймаут веде туди ж, куди й відповідь (у n8n вихід один, а відповіді немає).
                            if (from.Node.Suffix is { MirrorTimeout: true } && k == 0)
                            {
                                edges.Add((from.Tail, 1, to.Head, input));
                            }
                        }
                    }
                }
            }

            if (otherTypes.Count > 0)
            {
                _notes.Add(new(Check, null, $"З'єднання підвузлів n8n ({string.Join(", ", otherTypes)}) не переносяться: в AI_BPMS модель і tools задаються в самому вузлі ai.llm / ai.agent."));
            }

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (nodeName, reference) in ids)
            {
                names[reference.Id] = nodeName;
                names.TryAdd(reference.Head, nodeName);
                names.TryAdd(reference.Tail, nodeName);
            }
            var result = new JsonArray();
            var kept = new List<(string From, int Output, string To, int Input)>();
            foreach (var edge in edges.Distinct())
            {
                // Цикли в AI_BPMS заборонені (DAG): ребро, що замикає цикл, відкидаємо.
                if (Reaches(kept, edge.To, edge.From))
                {
                    _notes.Add(new(Check, names[edge.From], $"Зв'язок «{names[edge.From]}» → «{names[edge.To]}» утворює цикл і не перенесений: цикли в AI_BPMS заборонені, повторення — через core.loop."));
                    continue;
                }

                kept.Add(edge);
                result.Add(new JsonObject
                {
                    ["from"] = new JsonObject { ["node"] = edge.From, ["output"] = edge.Output },
                    ["to"] = new JsonObject { ["node"] = edge.To, ["input"] = edge.Input },
                });
            }

            return result;
        }

        private static bool Reaches(List<(string From, int Output, string To, int Input)> edges, string start, string target)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { start };
            var stack = new Stack<string>([start]);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == target)
                {
                    return true;
                }

                foreach (var e in edges.Where(e => e.From == current && seen.Add(e.To)))
                {
                    stack.Push(e.To);
                }
            }

            return false;
        }

        private JsonObject PinData(Dictionary<string, NodeRef> ids)
        {
            var result = new JsonObject();
            if (source["pinData"] is not JsonObject pins)
            {
                return result;
            }

            foreach (var (name, items) in pins)
            {
                if (!ids.TryGetValue(name, out var node) || items is not JsonArray list)
                {
                    continue;
                }

                result[node.Tail] = new JsonArray([.. list.OfType<JsonObject>().Select(i => (JsonNode)new JsonObject
                {
                    ["json"] = (i["json"] is JsonObject json ? json : i).DeepClone(),
                })]);
            }

            return result;
        }

        private Mapped? MapNode(JsonObject n8n, string type, string name, out string? reason)
        {
            reason = null;
            var p = n8n["parameters"] as JsonObject ?? [];
            void Note(string message) => _notes.Add(new(Check, name, message));
            JsonNode? E(JsonNode? value) => Expr(value, Note);

            switch (type)
            {
                case "n8n-nodes-base.manualTrigger":
                    return new("core.manualTrigger", 1, [], 1);

                case "n8n-nodes-base.scheduleTrigger":
                    return Schedule(p, Note, out reason);

                case "n8n-nodes-base.webhook":
                    return Webhook(p, Note);

                case "n8n-nodes-base.code":
                    if (Str(p["language"]) is { } language && language != "javaScript")
                    {
                        reason = $"Код мовою {language} не підтримується: core.code виконує лише JavaScript.";
                        return null;
                    }

                    var eachItem = Str(p["mode"]) == "runOnceForEachItem";
                    var code = Str(p["jsCode"]) ?? "return items;";
                    var rewritten = RewriteLuxon(code, "new Date()", Timezone, Note);
                    if (rewritten != code)
                    {
                        Note("Дату Luxon ($now.setZone(…).toISODate()/toFormat(…), $today) в коді замінено еквівалентом на new Date() (пояс сценарію).");
                        code = rewritten;
                    }

                    CodeNotes(code, Note);
                    return new("core.code", 1, new JsonObject { ["code"] = CodeShim(code, eachItem), ["mode"] = eachItem ? "eachItem" : "allItems" }, 1);

                case "n8n-nodes-base.if":
                    return If(p, E, Note);

                case "n8n-nodes-base.merge":
                    return Merge(p, Note);

                case "n8n-nodes-base.set":
                    return Set(n8n, p, E, out reason);

                case "n8n-nodes-base.noOp":
                    return new("core.set", 1, new JsonObject { ["fields"] = new JsonArray() }, 1);

                case "n8n-nodes-base.wait":
                    return Wait(p, E, out reason);

                case "n8n-nodes-base.dataTable":
                    return DataTable(p, E, Note, out reason);

                case "n8n-nodes-base.rssFeedRead":
                    return RssRead(p, E);

                case "n8n-nodes-base.httpRequest":
                    return HttpRequest(name, p, E, Note);

                case "n8n-nodes-base.telegram":
                    return Telegram(n8n, p, E, Note, out reason);

                case "@n8n/n8n-nodes-langchain.ollama":
                    return Ollama(n8n, p, E, Note, out reason);

                default:
                    reason = UnsupportedReason(type);
                    return null;
            }
        }

        private static string UnsupportedReason(string type) => type switch
        {
            "n8n-nodes-base.rssFeedReadTrigger" =>
                "Тригер RSS не переноситься: поставте core.schedule, далі mcp.tool rss_read (конектор «HTTP і RSS») і відсікайте вже бачені записи таблицею даних чи кодом.",
            _ when type.StartsWith("@n8n/n8n-nodes-langchain.", StringComparison.Ordinal) =>
                "ШІ-вузол n8n без прямого відповідника: замініть на ai.llm (одна відповідь моделі) або ai.agent (агент з tools).",
            _ => "Вузол n8n без відповідника в AI_BPMS: замініть його вузлом AI_BPMS або mcp.tool відповідного конектора.",
        };

        private Mapped Placeholder(string type, string name, string reason)
        {
            _notes.Add(new(Replace, name, $"{type}: {reason}"));
            var code = $"throw new Error({JsonSerializer.Serialize($"Вузол «{name}» ({type}) не перенесено з n8n — замініть його. {reason}", Indented)});";
            return new("core.code", 1, new JsonObject { ["code"] = code, ["mode"] = "allItems" }, 1);
        }

        private string Timezone =>
            Str(source["settings"]?["timezone"]) is { Length: > 0 } tz && tz != "DEFAULT" ? tz : "Europe/Kyiv";

        private JsonNode? Expr(JsonNode? value, Action<string> note) => value switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Expression(s, note, Timezone)),
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Expr(p.Value, note)))),
            JsonArray a => new JsonArray([.. a.Select(i => Expr(i, note))]),
            null => null,
            _ => value.DeepClone(),
        };

        private static void CodeNotes(string code, Action<string> note)
        {
            var found = new List<string>();
            foreach (var (token, label) in new[]
            {
                ("Buffer", "Buffer"), ("require(", "require"), ("$binary", "$binary"), (".binary", "бінарні дані"),
                ("this.helpers", "this.helpers"), ("DateTime", "Luxon DateTime"), ("$now", "$now"), ("$today", "$today"),
                ("$workflow", "$workflow"), ("$execution", "$execution"), ("$env", "$env"), ("fetch(", "fetch"),
                ("$items(", "$items()"), ("$jmespath", "$jmespath"),
            })
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    found.Add(label);
                }
            }

            note(found.Count > 0
                ? $"Код використовує те, чого в пісочниці AI_BPMS немає: {string.Join(", ", found.Distinct())} — перепишіть ці місця. Ліміт виконання коду — 5 с."
                : "Код перенесено з емуляцією $input і $('…'); перевірте його тестовим запуском. Ліміт виконання коду — 5 с.");
        }

        private static Mapped? Schedule(JsonObject p, Action<string> note, out string? reason)
        {
            reason = null;
            var intervals = (p["rule"]?["interval"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            if (intervals.Count == 0)
            {
                intervals.Add(new JsonObject { ["field"] = "days" });
            }

            if (intervals.Count > 1)
            {
                note($"У n8n {intervals.Count} правил розкладу, перенесено перше: для решти додайте окремі сценарії або один cron.");
            }

            var r = intervals[0];
            var minute = Int(r["triggerAtMinute"]) ?? 0;
            var hour = Int(r["triggerAtHour"]) ?? 0;
            string? cron = (Str(r["field"]) ?? "days") switch
            {
                "cronExpression" => Str(r["expression"])?.Trim(),
                "seconds" => $"0/{Math.Max(Int(r["secondsInterval"]) ?? 30, 1)} * * * * ?",
                "minutes" => $"*/{Math.Max(Int(r["minutesInterval"]) ?? 5, 1)} * * * *",
                "hours" => $"{minute} */{Math.Max(Int(r["hoursInterval"]) ?? 1, 1)} * * *",
                "days" => (Int(r["daysInterval"]) ?? 1) is var d && d > 1 ? $"{minute} {hour} */{d} * *" : $"{minute} {hour} * * *",
                "weeks" => $"{minute} {hour} * * {Days(r["triggerAtDay"])}",
                "months" => $"{minute} {hour} {Int(r["triggerAtDayOfMonth"]) ?? 1} */{Math.Max(Int(r["monthsInterval"]) ?? 1, 1)} *",
                _ => null,
            };
            if ((Str(r["field"]) == "weeks" && (Int(r["weeksInterval"]) ?? 1) > 1))
            {
                note("Інтервал «кожні N тижнів» cron не виражає: розклад спрацьовуватиме щотижня.");
            }

            if (string.IsNullOrWhiteSpace(cron))
            {
                reason = "Правило розкладу n8n не вдалося перетворити на cron.";
                return null;
            }

            return new("core.schedule", 1, new JsonObject { ["cron"] = cron }, 1);
        }

        private static string Days(JsonNode? days)
        {
            var list = (days as JsonArray)?.Select(d => Int(d)).OfType<int>().Distinct().Order().ToList() ?? [];
            return list.Count == 0 ? "0" : string.Join(',', list);
        }

        private static Mapped Webhook(JsonObject p, Action<string> note)
        {
            var raw = Str(p["path"]) ?? "";
            var path = PathCharsRegex().Replace(raw.Trim('/'), "-").Trim('-', '/');
            if (path.Length == 0)
            {
                path = $"n8n-{Guid.NewGuid():N}"[..16];
            }

            if (path != raw.Trim('/'))
            {
                note($"Шлях webhook-а «{raw}» змінено на «{path}»: в AI_BPMS шлях без параметрів і пробілів.");
            }

            var method = (Str(p["httpMethod"]) ?? "GET").ToUpperInvariant();
            var parameters = new JsonObject
            {
                ["method"] = method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" ? method : "POST",
                ["path"] = path,
                ["respond"] = Str(p["responseMode"]) switch
                {
                    "lastNode" => "whenFinished",
                    "responseNode" => "fromNode",
                    _ => "immediately",
                },
            };
            note("Адреса webhook-а в AI_BPMS інша (/webhook/<шлях>): оновіть її в системі, що викликає сценарій.");
            return new("core.webhook", 1, parameters, 1);
        }

        private static Mapped If(JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note)
        {
            var conditions = new JsonArray();
            var combine = "and";
            if (p["conditions"]?["conditions"] is JsonArray v2)
            {
                combine = Str(p["conditions"]?["combinator"]) == "or" ? "or" : "and";
                foreach (var c in v2.OfType<JsonObject>())
                {
                    var operation = Str(c["operator"]?["operation"]) ?? "equals";
                    var mapped = operation switch
                    {
                        "equals" or "notEquals" or "gt" or "gte" or "lt" or "lte" or "contains" or "notContains" or "startsWith" or "endsWith" => operation,
                        "true" => "isTrue",
                        "false" => "isFalse",
                        "empty" or "notExists" => "isEmpty",
                        "notEmpty" or "exists" => "isNotEmpty",
                        "after" => "gt",
                        "before" => "lt",
                        "afterOrEquals" => "gte",
                        "beforeOrEquals" => "lte",
                        _ => null,
                    };
                    if (mapped is null)
                    {
                        note($"Оператор n8n «{Str(c["operator"]?["type"])}.{operation}» не має відповідника — умову замінено на «дорівнює», виправте її.");
                        mapped = "equals";
                    }

                    var condition = new JsonObject { ["left"] = e(c["leftValue"]), ["operator"] = mapped };
                    if (mapped is not ("isTrue" or "isFalse" or "isEmpty" or "isNotEmpty"))
                    {
                        condition["right"] = e(c["rightValue"]);
                    }

                    conditions.Add(condition);
                }
            }
            else if (p["conditions"] is JsonObject v1)
            {
                // IF v1: { boolean: [...], number: [...], string: [...] } з value1/operation/value2.
                combine = Str(p["combineOperation"]) == "any" ? "or" : "and";
                foreach (var (_, group) in v1)
                {
                    foreach (var c in (group as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var operation = Str(c["operation"]) ?? "equal";
                        var mapped = operation switch
                        {
                            "equal" => "equals",
                            "notEqual" => "notEquals",
                            "larger" => "gt",
                            "largerEqual" => "gte",
                            "smaller" => "lt",
                            "smallerEqual" => "lte",
                            "contains" or "notContains" or "startsWith" or "endsWith" or "isEmpty" or "isNotEmpty" => operation,
                            _ => null,
                        };
                        if (mapped is null)
                        {
                            note($"Оператор n8n «{operation}» не має відповідника — умову замінено на «дорівнює», виправте її.");
                            mapped = "equals";
                        }

                        var condition = new JsonObject { ["left"] = e(c["value1"]), ["operator"] = mapped };
                        if (mapped is not ("isEmpty" or "isNotEmpty"))
                        {
                            condition["right"] = e(c["value2"]) ?? (JsonNode?)JsonValue.Create("");
                        }

                        conditions.Add(condition);
                    }
                }
            }

            return new("core.if", 1, new JsonObject { ["conditions"] = conditions, ["combine"] = combine }, 2);
        }

        private static Mapped Merge(JsonObject p, Action<string> note)
        {
            var mode = Str(p["mode"]) ?? "append";
            var combine = "append";
            if (mode == "combine" && (Str(p["combineBy"]) ?? Str(p["combinationMode"])) is "combineByPosition" or "mergeByIndex")
            {
                combine = "combineByPosition";
            }
            else if (mode == "mergeByIndex")
            {
                combine = "combineByPosition";
            }
            else if (mode != "append")
            {
                note($"Режим злиття n8n «{mode}» не має відповідника — items гілок додаються одна за одною (append).");
            }

            var branches = Math.Clamp(Int(p["numberInputs"]) ?? 2, 2, 8);
            return new("core.merge", 2, new JsonObject { ["mode"] = "all", ["branches"] = branches, ["combine"] = combine }, 1);
        }

        private static Mapped? Set(JsonObject n8n, JsonObject p, Func<JsonNode?, JsonNode?> e, out string? reason)
        {
            reason = null;
            var fields = new JsonArray();
            bool keepOnlySet;
            if (p["assignments"]?["assignments"] is JsonArray assignments)
            {
                foreach (var a in assignments.OfType<JsonObject>())
                {
                    if (Str(a["name"]) is { Length: > 0 } field)
                    {
                        fields.Add(new JsonObject { ["name"] = field, ["value"] = e(a["value"]) });
                    }
                }

                keepOnlySet = (n8n["typeVersion"] is JsonValue tv && tv.TryGetValue<double>(out var version) ? version : 3) >= 3.3
                    ? !Bool(p["includeOtherFields"])
                    : Str(p["include"]) == "none";
            }
            else if (Str(p["mode"]) == "raw")
            {
                reason = "Режим JSON (raw) вузла Set не переноситься: задайте поля в core.set або поверніть об'єкт з core.code.";
                return null;
            }
            else
            {
                foreach (var (_, group) in p["values"] as JsonObject ?? [])
                {
                    foreach (var v in (group as JsonArray ?? []).OfType<JsonObject>())
                    {
                        if (Str(v["name"]) is { Length: > 0 } field)
                        {
                            fields.Add(new JsonObject { ["name"] = field, ["value"] = e(v["value"]) });
                        }
                    }
                }

                keepOnlySet = Bool(p["keepOnlySet"]);
            }

            return new("core.set", 1, new JsonObject { ["fields"] = fields, ["keepOnlySet"] = keepOnlySet }, 1);
        }

        private static Mapped? Wait(JsonObject p, Func<JsonNode?, JsonNode?> e, out string? reason)
        {
            reason = null;
            switch (Str(p["resume"]) ?? "timeInterval")
            {
                case "timeInterval":
                    var unit = Str(p["unit"]) ?? "hours";
                    return new("core.wait", 2, new JsonObject
                    {
                        ["mode"] = "duration",
                        ["amount"] = p["amount"] is JsonValue a && a.TryGetValue<double>(out var amount) ? amount : 1,
                        ["unit"] = unit is "seconds" or "minutes" or "hours" or "days" ? unit : "hours",
                    }, 1);
                case "specificTime":
                    return new("core.wait", 2, new JsonObject { ["mode"] = "until", ["until"] = e(p["dateTime"]) }, 1);
                default:
                    reason = "Очікування webhook-а чи форми з вузла Wait не переноситься: використайте mcp.waitFor або окремий сценарій з core.webhook.";
                    return null;
            }
        }

        private Mapped? DataTable(JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note, out string? reason)
        {
            reason = null;
            var table = Locator(p["dataTableId"], preferName: true);
            if (string.IsNullOrWhiteSpace(table))
            {
                reason = "Не вказано таблицю даних.";
                return null;
            }

            var columns = _tables.TryGetValue(table, out var set) ? set : _tables[table] = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var c in (p["columns"]?["schema"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Str(c["id"]) is { Length: > 0 } column)
                {
                    columns.Add(column);
                }
            }

            JsonObject Values()
            {
                var values = new JsonObject();
                if (Str(p["columns"]?["mappingMode"]) == "autoMapInputData")
                {
                    foreach (var column in columns)
                    {
                        values[column] = $"{{{{ $json[\"{column}\"] }}}}";
                    }
                }
                else
                {
                    foreach (var (column, value) in p["columns"]?["value"] as JsonObject ?? [])
                    {
                        values[column] = e(value);
                        columns.Add(column);
                    }
                }

                return values;
            }

            var parameters = new JsonObject { ["table"] = table };
            switch (Str(p["operation"]) ?? "insert")
            {
                case "insert":
                    parameters["operation"] = "insert";
                    parameters["values"] = Values();
                    break;
                case "get":
                    parameters["operation"] = "list";
                    var where = new JsonArray();
                    foreach (var c in (p["filters"]?["conditions"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var op = (Str(c["condition"]) ?? "eq") switch
                        {
                            "eq" => "eq",
                            "neq" => "ne",
                            "gt" => "gt",
                            "gte" => "gte",
                            "lt" => "lt",
                            "lte" => "lte",
                            "like" or "ilike" => "contains",
                            "isEmpty" => "isEmpty",
                            "isNotEmpty" => "isNotEmpty",
                            var other => other,
                        };
                        if (Str(c["keyName"]) is not { Length: > 0 } column || op is not ("eq" or "ne" or "gt" or "gte" or "lt" or "lte" or "contains" or "isEmpty" or "isNotEmpty"))
                        {
                            note($"Умову фільтра «{Str(c["keyName"])} {op}» не перенесено.");
                            continue;
                        }

                        var filter = new JsonObject { ["column"] = column, ["operator"] = op };
                        if (op is not ("isEmpty" or "isNotEmpty"))
                        {
                            filter["value"] = e(c["keyValue"]);
                        }

                        where.Add(filter);
                    }

                    if (where.Count > 0)
                    {
                        parameters["where"] = where;
                        if (Str(p["matchType"]) == "anyCondition")
                        {
                            note("Фільтр «будь-яка умова» не підтримується: core.dataTable перевіряє всі умови разом.");
                        }
                    }

                    var returnAll = Bool(p["returnAll"]);
                    parameters["limit"] = returnAll ? 1000 : Math.Clamp(Int(p["limit"]) ?? 50, 1, 1000);
                    if (returnAll)
                    {
                        note("«Return All» → не більше 1000 рядків (межа core.dataTable).");
                    }

                    break;
                case "update" or "upsert":
                    parameters["operation"] = Str(p["operation"]);
                    parameters["values"] = Values();
                    note("Оновлення в n8n шукає рядки за фільтром, а core.dataTable — за ключовою колонкою: перевірте key і ключову колонку таблиці.");
                    break;
                case "deleteRows":
                    parameters["operation"] = "delete";
                    parameters["key"] = "";
                    note("Видалення в n8n — за фільтром, у core.dataTable — за ключем: вкажіть key.");
                    break;
                default:
                    reason = $"Операції «{Str(p["operation"])}» таблиці даних в AI_BPMS немає.";
                    return null;
            }

            return new("core.dataTable", 1, parameters, 1);
        }

        private string HttpConnector()
        {
            _connectors.TryAdd(HttpReference, new ManifestConnector(HttpReference, "HTTP і RSS", ""));
            return HttpReference;
        }

        private string TelegramConnector(JsonObject n8n)
        {
            var credential = n8n["credentials"]?["telegramApi"];
            var reference = $"n8n:telegramApi:{Str(credential?["id"]) ?? "default"}";
            _connectors.TryAdd(reference, new ManifestConnector(reference, Str(credential?["name"]) ?? "Telegram", ""));
            return reference;
        }

        private Mapped RssRead(JsonObject p, Func<JsonNode?, JsonNode?> e) =>
            new("mcp.tool", 1, new JsonObject
            {
                ["serverId"] = HttpConnector(),
                ["toolName"] = "rss_read",
                ["arguments"] = new JsonObject { ["url"] = e(p["url"]) },
                ["itemsPath"] = "items",
            }, 1);

        private Mapped HttpRequest(string name, JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note)
        {
            var arguments = new JsonObject
            {
                ["url"] = e(p["url"]),
                ["method"] = (Str(p["method"]) ?? "GET").ToUpperInvariant(),
            };
            var options = p["options"] as JsonObject ?? [];
            var response = options["response"]?["response"] as JsonObject ?? [];

            JsonObject Pairs(JsonNode? list)
            {
                var result = new JsonObject();
                foreach (var pair in (list?["parameters"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (Str(pair["name"]) is { Length: > 0 } key)
                    {
                        result[key] = e(pair["value"]);
                    }
                }

                return result;
            }

            var query = Bool(p["sendQuery"]) ? Pairs(p["queryParameters"]) : [];
            if (Bool(p["sendQuery"]) && Str(p["specifyQuery"]) == "json")
            {
                note("Параметри запиту, задані JSON-ом (specifyQuery), не перенесено: заповніть query вручну.");
            }

            if (query.Count > 0)
            {
                arguments["query"] = query;
            }

            var headers = Bool(p["sendHeaders"]) ? Pairs(p["headerParameters"]) : [];
            if (Bool(p["sendBody"]))
            {
                var contentType = Str(p["contentType"]) ?? "json";
                switch (contentType)
                {
                    case "json" when (Str(p["specifyBody"]) ?? "keypair") == "json":
                        arguments["body"] = e(p["jsonBody"]);
                        headers["Content-Type"] ??= "application/json";
                        break;
                    case "json":
                        var json = Pairs(p["bodyParameters"]);
                        if (json.Count > 0)
                        {
                            arguments["json"] = json;
                        }

                        break;
                    case "raw":
                        arguments["body"] = e(p["body"]);
                        headers["Content-Type"] ??= Str(p["rawContentType"]) ?? "text/plain";
                        break;
                    default:
                        note($"Тіло запиту типу «{contentType}» (форма, файли) не перенесено: http_request надсилає json або текст — задайте body вручну.");
                        break;
                }
            }

            if (headers.Count > 0)
            {
                arguments["headers"] = headers;
            }

            if (Str(p["authentication"]) is { Length: > 0 } auth && auth != "none")
            {
                note("Авторизацію n8n (credential) не перенесено: додайте заголовок Authorization (чи ключ у query) у вузол.");
            }

            int? timeoutSec = null;
            if (options["timeout"] is JsonValue ms && ms.TryGetValue<double>(out var millis) && millis > 0)
            {
                var seconds = (int)Math.Clamp(Math.Ceiling(millis / 1000), 1, 300);
                arguments["timeoutSec"] = seconds;
                timeoutSec = seconds + 10;
            }

            if (Bool(response["neverError"]))
            {
                arguments["fail_on_status_error"] = false;
            }

            var full = Bool(response["fullResponse"]);
            if (full || Str(response["responseFormat"]) == "file")
            {
                note("Відповідь повертається як { status, headers, json | text | base64 } (не форма n8n «full response» чи файл): перевірте вузли, що її читають.");
            }

            var parameters = new JsonObject { ["serverId"] = HttpConnector(), ["toolName"] = "http_request", ["arguments"] = arguments };
            ExtraNode? unwrap = null;
            if (!full)
            {
                unwrap = new ExtraNode(
                    $"{name}: тіло відповіді", "core.code", 1,
                    new JsonObject
                    {
                        ["code"] = "// n8n віддає тіло відповіді: JSON — полями item'а, текст чи base64 — у data.\n" +
                                   "return items.map((r) => (r.json !== undefined && r.json !== null\n" +
                                   "  ? (typeof r.json === 'object' && !Array.isArray(r.json) ? r.json : { data: r.json })\n" +
                                   "  : { data: r.text !== undefined ? r.text : r.base64 }));",
                        ["mode"] = "allItems",
                    },
                    1);
                note($"Відповідь http_request ({{ status, json | text | base64 }}) розгорнуто вузлом core.code «{unwrap.Name}» у форму n8n: JSON-тіло — полями item'а.");
            }

            return new("mcp.tool", 1, parameters, 1, Suffix: unwrap, TimeoutSec: timeoutSec);
        }

        private Mapped? Telegram(JsonObject n8n, JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note, out string? reason)
        {
            reason = null;
            var resource = Str(p["resource"]) ?? "message";
            var operation = Str(p["operation"]) ?? "sendMessage";
            if (resource == "message" && operation == "sendPhoto")
            {
                return SendPhoto(n8n, p, e, note);
            }

            if (resource == "message" && operation == "sendAndWait")
            {
                return SendAndWait(n8n, p, e, note);
            }

            if (resource != "message" || operation != "sendMessage")
            {
                reason = $"Операцію Telegram «{resource}.{operation}» не перенесено: див. tools конектора Telegram (send_message, send_photo, send_document, edit_message_text, send_approval…).";
                return null;
            }

            var reference = TelegramConnector(n8n);
            var fields = p["additionalFields"] as JsonObject ?? [];
            var arguments = new JsonObject { ["chat_id"] = e(p["chatId"]), ["text"] = e(p["text"]) };
            if (Str(fields["parse_mode"]) is { Length: > 0 } parseMode)
            {
                arguments["parse_mode"] = parseMode;
            }

            if (Bool(fields["disable_web_page_preview"]))
            {
                arguments["disable_link_preview"] = true;
            }

            if (p["replyMarkup"] is not null && Str(p["replyMarkup"]) != "none")
            {
                note("Клавіатуру (reply markup) не перенесено: задайте reply_markup у send_message.");
            }

            return new("mcp.tool", 1, new JsonObject { ["serverId"] = reference, ["toolName"] = TelegramTool, ["arguments"] = arguments }, 1);
        }

        private Mapped SendPhoto(JsonObject n8n, JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note)
        {
            var reference = TelegramConnector(n8n);
            var fields = p["additionalFields"] as JsonObject ?? [];
            var arguments = new JsonObject { ["chat_id"] = e(p["chatId"]) };
            if (Bool(p["binaryData"]))
            {
                arguments["photo_base64"] = "{{ $json.image_base64 }}";
                note("Картинка в n8n бралась з бінарного поля, а send_photo приймає base64 у photo_base64: у вузол підставлено {{ $json.image_base64 }} — вкажіть поле з base64 картинки (наприклад, images[0] з відповіді генератора).");
            }
            else
            {
                arguments["photo_url"] = e(p["file"]);
            }

            if (e(fields["caption"]) is { } caption)
            {
                arguments["caption"] = caption;
            }

            if (Str(fields["parse_mode"]) is { Length: > 0 } parseMode)
            {
                arguments["parse_mode"] = parseMode;
            }

            if (p["replyMarkup"] is not null && Str(p["replyMarkup"]) != "none")
            {
                note("Клавіатуру (reply markup) не перенесено: задайте reply_markup у send_photo.");
            }

            return new("mcp.tool", 1, new JsonObject { ["serverId"] = reference, ["toolName"] = "send_photo", ["arguments"] = arguments }, 1);
        }

        private Mapped SendAndWait(JsonObject n8n, JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note)
        {
            var reference = TelegramConnector(n8n);
            var name = Str(n8n["name"]) ?? "Погодження";
            var approval = p["approvalOptions"]?["values"] as JsonObject ?? [];
            var arguments = new JsonObject { ["chat_id"] = e(p["chatId"]), ["text"] = e(p["message"]) };
            if (Str(approval["approveLabel"]) is { Length: > 0 } approve)
            {
                arguments["approve_label"] = approve;
            }

            if (Str(approval["disapproveLabel"]) is { Length: > 0 } reject)
            {
                arguments["reject_label"] = reject;
            }

            if (Str(approval["approvalType"]) == "single")
            {
                note("Погодження з однією кнопкою (single) перенесено з двома: «погодити» і «відхилити».");
            }

            // Ліміт очікування n8n (limitWaitTime): за замовчуванням чекає без обмеження, а Telegram тримає оновлення добу.
            var wait = n8n["parameters"]?["options"]?["limitWaitTime"]?["values"] as JsonObject;
            var hours = 24.0;
            if (wait is not null && wait["resumeAmount"] is JsonValue amount && amount.TryGetValue<double>(out var value) && value > 0)
            {
                hours = Str(wait["resumeUnit"]) switch
                {
                    "minutes" => value / 60,
                    "days" => value * 24,
                    _ => value,
                };
            }

            if (hours > 24)
            {
                note($"Очікування відповіді {hours:0.#} год скорочено до 24: Telegram тримає непідтверджені оновлення добу (get_approval читає їх через getUpdates).");
                hours = 24;
            }
            else if (wait is null)
            {
                note("У n8n очікування не обмежене, в AI_BPMS — доба (Telegram тримає непідтверджені оновлення 24 год).");
            }

            note("«Send and Wait» перенесено двома вузлами: mcp.tool send_approval і mcp.waitFor get_approval (кожну хвилину, умова answered). Вихід «Без відповіді» (timeout) з'єднано туди ж, куди й відповідь: вираз $json.data.approved переписано на $json.approved (без відповіді поля немає, тож це «не погоджено»). Бот не повинен мати webhook (get_approval читає оновлення через getUpdates).");
            var waitFor = new ExtraNode(
                $"{name}: очікування відповіді", "mcp.waitFor", 1,
                new JsonObject
                {
                    ["serverId"] = reference,
                    ["toolName"] = "get_approval",
                    ["arguments"] = new JsonObject
                    {
                        ["chat_id"] = "{{ $json.chat_id }}",
                        ["message_id"] = "{{ $json.message_id }}",
                        ["token"] = "{{ $json.token }}",
                    },
                    ["conditions"] = new JsonArray(new JsonObject { ["field"] = "answered", ["operator"] = "isTrue" }),
                    ["intervalMinutes"] = 1,
                    ["timeoutHours"] = hours,
                },
                2,
                MirrorTimeout: true);
            return new("mcp.tool", 1, new JsonObject { ["serverId"] = reference, ["toolName"] = "send_approval", ["arguments"] = arguments }, 1, Suffix: waitFor);
        }

        private Mapped? Ollama(JsonObject n8n, JsonObject p, Func<JsonNode?, JsonNode?> e, Action<string> note, out string? reason)
        {
            reason = null;
            var model = Locator(p["modelId"]) ?? Str(p["model"]);
            var options = p["options"] as JsonObject ?? [];
            var system = new List<string>();
            var prompt = new List<string>();
            if (Str(options["system"]) is { Length: > 0 } s)
            {
                system.Add(Str(e(s))!);
            }

            foreach (var m in (p["messages"]?["values"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var content = Str(e(m["content"])) ?? "";
                switch (Str(m["role"]) ?? "user")
                {
                    case "system":
                        system.Add(content);
                        break;
                    case "assistant":
                        note("Повідомлення з роллю assistant не перенесено: ai.llm приймає system і prompt.");
                        break;
                    default:
                        prompt.Add(content);
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(model) || prompt.All(string.IsNullOrWhiteSpace))
            {
                reason = "У вузлі Ollama немає моделі або повідомлення користувача.";
                return null;
            }

            var credential = n8n["credentials"]?["ollamaApi"];
            var reference = $"n8n:ollamaApi:{Str(credential?["id"]) ?? "default"}";
            _credentials.TryAdd(reference, new ManifestCredential(reference, Str(credential?["name"]) ?? "Ollama", "openai-compatible"));

            var parameters = new JsonObject
            {
                ["provider"] = "openai-compatible",
                ["model"] = model,
                ["credentialId"] = reference,
                ["prompt"] = string.Join("\n\n", prompt.Where(t => t.Length > 0)),
            };
            if (system.Count > 0)
            {
                parameters["system"] = string.Join("\n\n", system);
            }

            if (options["temperature"] is JsonValue t && t.TryGetValue<double>(out var temperature))
            {
                parameters["temperature"] = Math.Clamp(temperature, 0, 2);
            }

            note("Ollama → ai.llm (openai-compatible): у credential вкажіть адресу Ollama з /v1 (напр. http://host.docker.internal:11434/v1). Відповідь моделі тепер у $json.text — перевірте вузли, що її читають.");
            return new("ai.llm", 1, parameters, 1);
        }
    }
}
