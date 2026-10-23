using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Connector.Template.Generated;
using Microsoft.OpenApi;
using ModelContextProtocol.Protocol;

namespace Connector.Template.OpenApi;

/// <summary>
/// Tool на кожну операцію OpenAPI: назва — <c>operationId</c> у snake_case (або <c>&lt;method&gt;_&lt;path&gt;</c>),
/// <c>description</c> — <c>summary</c> (і <c>description</c>), <c>inputSchema</c> — параметри path/query/header за назвами
/// та JSON-тіло у властивості <c>body</c>. Операції з тілом не-JSON пропускаються (див. <see cref="Result.Skipped"/>).
/// </summary>
public static class OpenApiToolFactory
{
    /// <summary>Аргумент із тілом запиту.</summary>
    public const string BodyArgument = "body";

    /// <summary>Згенеровані tools і пропущені операції з причиною.</summary>
    /// <param name="Tools">Tools у порядку операцій у документі.</param>
    /// <param name="Skipped">«METHOD /path — причина».</param>
    public sealed record Result(IReadOnlyList<OpenApiTool> Tools, IReadOnlyList<string> Skipped);

    /// <summary>Будує tools.</summary>
    /// <param name="document">Специфікація.</param>
    /// <param name="filter">Які операції брати.</param>
    /// <param name="rest">Клієнт REST API для виклику (базова адреса й автентифікація конектора).</param>
    public static Result Create(OpenApiDocument document, OpenApiOptions filter, Func<RestClient> rest)
    {
        var inliner = new JsonSchemaInliner(document);
        var include = filter.Include.Select(Matcher.Parse).ToList();
        var exclude = filter.Exclude.Select(Matcher.Parse).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tools = new List<OpenApiTool>();
        var skipped = new List<string>();

        foreach (var (path, item) in document.Paths ?? [])
        {
            foreach (var (method, operation) in item.Operations ?? [])
            {
                var tags = operation.Tags?.Select(t => t.Name).OfType<string>().ToList() ?? [];
                if ((include.Count > 0 && !include.Any(m => m.Matches(path, tags))) || exclude.Any(m => m.Matches(path, tags)))
                {
                    continue;
                }

                var title = $"{method.Method.ToUpperInvariant()} {path}";
                if (!TryBind(method, path, item, operation, inliner, out var binding, out var schema, out var reason))
                {
                    skipped.Add($"{title} — {reason}");
                    continue;
                }

                var name = ToolNaming.Unique(ToolNaming.ToolName(operation.OperationId) ?? ToolNaming.ToolName($"{method.Method}_{path}") ?? "operation", names);
                var readOnly = method == HttpMethod.Get || method == HttpMethod.Head;
                var tool = new Tool
                {
                    Name = name,
                    Title = operation.Summary ?? title,
                    Description = Describe(operation, title),
                    InputSchema = JsonSerializer.SerializeToElement(schema),
                    Annotations = new ToolAnnotations
                    {
                        ReadOnlyHint = readOnly,
                        DestructiveHint = readOnly ? null : method == HttpMethod.Delete,
                        IdempotentHint = readOnly || method == HttpMethod.Put || method == HttpMethod.Delete,
                        OpenWorldHint = true,
                    },
                };
                tools.Add(new OpenApiTool(tool, binding, rest));
            }
        }

        return new Result(tools, skipped);
    }

    private static bool TryBind(
        HttpMethod method,
        string path,
        IOpenApiPathItem item,
        OpenApiOperation operation,
        JsonSchemaInliner inliner,
        out OpenApiBinding binding,
        out JsonObject schema,
        out string? reason)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var parameters = new List<OpenApiBinding.Parameter>();
        binding = null!;
        schema = null!;
        reason = null;

        // Параметри рівня шляху, перекриті однойменними параметрами операції.
        var all = (item.Parameters ?? []).Concat(operation.Parameters ?? [])
            .Where(p => p.Name is not null && p.In is not null)
            .GroupBy(p => (p.Name, p.In))
            .Select(g => g.Last());
        foreach (var parameter in all)
        {
            if (parameter.In == ParameterLocation.Cookie)
            {
                continue;
            }

            var argument = properties.ContainsKey(parameter.Name!) ? $"{parameter.In!.Value.ToString().ToLowerInvariant()}_{parameter.Name}" : parameter.Name!;
            var isRequired = parameter.Required || parameter.In == ParameterLocation.Path;
            var property = parameter.Schema is { } s ? inliner.Inline(s).AsObject() : new JsonObject { ["type"] = "string" };
            if (parameter.Description is { Length: > 0 } description && !property.ContainsKey("description"))
            {
                property["description"] = description;
            }

            properties[argument] = property;
            if (isRequired)
            {
                required.Add(argument);
            }

            parameters.Add(new OpenApiBinding.Parameter(parameter.Name!, argument, parameter.In!.Value, isRequired));
        }

        string? bodyArgument = null;
        if (operation.RequestBody is { Content.Count: > 0 } body)
        {
            var json = body.Content.FirstOrDefault(c => IsJson(c.Key));
            if (json.Value is null)
            {
                reason = $"тіло {string.Join(", ", body.Content.Keys)} не підтримується (лише JSON)";
                return false;
            }

            bodyArgument = properties.ContainsKey(BodyArgument) ? "request_body" : BodyArgument;
            var property = json.Value.Schema is { } s ? inliner.Inline(s).AsObject() : new JsonObject();
            if (body.Description is { Length: > 0 } description && !property.ContainsKey("description"))
            {
                property["description"] = description;
            }

            properties[bodyArgument] = property;
            if (body.Required)
            {
                required.Add(bodyArgument);
            }
        }

        schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        binding = new OpenApiBinding(method, path, parameters, bodyArgument);
        return true;
    }

    private static bool IsJson(string mediaType) =>
        mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
        mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
        mediaType.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase);

    private static string Describe(OpenApiOperation operation, string title)
    {
        var parts = new[] { operation.Summary, operation.Description }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct()
            .ToList();
        return parts.Count > 0 ? string.Join("\n\n", parts) : title;
    }

    /// <summary>Елемент фільтра: <c>tag:&lt;тег&gt;</c> або шаблон шляху з <c>*</c>.</summary>
    private sealed record Matcher(string? Tag, Regex? Path)
    {
        public static Matcher Parse(string pattern) =>
            pattern.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)
                ? new Matcher(pattern[4..].Trim(), null)
                : new Matcher(null, new Regex("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

        public bool Matches(string path, IReadOnlyList<string> tags) =>
            Tag is not null ? tags.Contains(Tag, StringComparer.OrdinalIgnoreCase) : Path!.IsMatch(path);
    }
}
