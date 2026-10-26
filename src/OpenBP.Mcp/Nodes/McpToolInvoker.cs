using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenBP.Engine.Diagnostics;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Nodes;
using OpenBP.Expressions;
using OpenBP.Mcp.Client;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// Спільна частина <c>mcp.tool</c> і <c>mcp.poll</c>: аргументи → <c>tools/call</c> через пул реєстру → items
/// (docs/connectors.md §2, docs/architecture.md §3).
/// </summary>
internal sealed class McpToolInvoker(IMcpServerRegistry servers)
{
    /// <summary>
    /// Викликає tool з обчисленими параметрами одного item'а і перетворює результат на items:
    /// <c>structuredContent</c> → один item; інакше по item на кожен <c>content</c>-блок.
    /// </summary>
    public async Task<List<Item>> CallAsync(INodeContext ctx, McpCallParameters p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.ServerId) || string.IsNullOrWhiteSpace(p.ToolName))
        {
            throw new McpToolException("У вузлі не обрано конектор або дію. Відкрийте вузол і оберіть їх.");
        }

        var serverId = p.ServerId;
        var toolName = p.ToolName;
        var arguments = BuildArguments(ctx.Node, serverId, toolName, p.Arguments);
        EnsureRequired(serverId, toolName, arguments);
        var timeout = Timeout(ctx.Node);
        var serverName = servers.TryGetServer(serverId, out var config) ? config.Name : serverId;

        // Span виклику (F6-18): сервер і tool без аргументів і результату.
        using var span = OpenBpTelemetry.StartChild(OpenBpTelemetry.McpToolSpan, ActivityKind.Client);
        span?.SetTag(OpenBpTelemetry.McpServerId, serverId);
        span?.SetTag(OpenBpTelemetry.McpTool, toolName);
        CallToolResult result;
        try
        {
            result = await servers.RunAsync(
                serverId,
                (client, token) => client.CallToolAsync(new CallToolRequestParams { Name = toolName, Arguments = arguments }, token).AsTask(),
                timeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (OpenBpTelemetry.Fail(span, ex) && ex is McpProtocolException)
        {
            throw new McpToolException(
                $"Конектор «{serverName}» відхилив дію «{toolName}»: {FirstLine(ex.Message)}. Перевірте аргументи вузла або оновіть список дій у розділі «Конектори».",
                ex)
            {
                Details = ex.Message,
            };
        }
        catch (McpUnavailableException ex)
        {
            throw new McpToolException(
                $"{ex.Message.TrimEnd('.')}. Перевірте конектор у розділі «Конектори» (або збільште таймаут вузла) і повторіть запуск.",
                ex)
            {
                Details = ex.InnerException?.Message,
            };
        }
        catch (McpServerUnknownException ex)
        {
            throw new McpToolException("Конектор вузла не знайдено — можливо, його видалено. Оберіть конектор у вузлі заново.", ex)
            {
                Details = ex.Message,
            };
        }

        if (result.IsError == true)
        {
            span?.SetStatus(ActivityStatusCode.Error, "isError");
            var details = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            throw new McpToolException(
                details.Length == 0
                    ? $"Дія «{toolName}» конектора «{serverName}» не виконалась. Перевірте аргументи вузла і повторіть запуск."
                    : $"Дія «{toolName}» конектора «{serverName}» не виконалась: {FirstLine(details)}. Перевірте аргументи вузла і повторіть запуск.")
            {
                Details = details.Length == 0 ? null : details,
            };
        }

        var itemsPath = string.IsNullOrWhiteSpace(p.ItemsPath) ? null : p.ItemsPath;
        return await ToItemsAsync(ctx, result, itemsPath, toolName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Обов'язкові аргументи з <c>inputSchema</c> tool'а (якщо список tools сервера вже отримано) не порожні після
    /// обчислення виразів: інакше — «Поле «chat_id» порожнє. Вкажіть значення або вираз.» до виклику tool'а.
    /// </summary>
    private void EnsureRequired(string serverId, string toolName, Dictionary<string, JsonElement> arguments)
    {
        if (servers.GetToolsCache(serverId)?.Find(toolName)?.InputSchema["required"] is not JsonArray required)
        {
            return;
        }

        foreach (var name in required.Select(r => r?.GetValue<string>()).OfType<string>())
        {
            var empty = !arguments.TryGetValue(name, out var value)
                || value.ValueKind == JsonValueKind.Null
                || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
            if (empty)
            {
                throw new McpToolException($"Поле «{name}» порожнє. Вкажіть значення або вираз, що його повертає.");
            }
        }
    }

    /// <summary>Перший рядок тексту без крапки в кінці — для повідомлення; увесь текст — у Details.</summary>
    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line.TrimEnd('.');
    }

    /// <summary>Таймаут вузла: <c>timeoutSec</c> або <see cref="McpToolNode.DefaultTimeout"/>.</summary>
    private static TimeSpan Timeout(NodeDefinition node) =>
        node.TimeoutSec is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : McpToolNode.DefaultTimeout;

    /// <summary>
    /// Обчислені аргументи як словник для <c>tools/call</c>. Поле, яке у сценарії було виразом <c>{{ }}</c>,
    /// приводиться до типу з <c>inputSchema</c> tool'а (якщо список tools сервера вже отримано).
    /// </summary>
    private Dictionary<string, JsonElement> BuildArguments(NodeDefinition node, string serverId, string toolName, JsonNode? evaluated)
    {
        var raw = node.Parameters["arguments"];
        var rawText = raw is JsonValue rv && rv.TryGetValue<string>(out var t) ? t : null;

        if (evaluated is JsonValue value && value.TryGetValue<string>(out var text) && rawText is not null)
        {
            // Увесь arguments — вираз, що повернув рядок: пробуємо прочитати його як JSON-об'єкт.
            evaluated = Coerce(rawText, JsonValue.Create(text), new JsonObject { ["type"] = "object" });
        }

        var args = evaluated switch
        {
            null => [],
            JsonObject obj => obj,
            _ => throw new McpToolException(
                $"Аргументи дії мають бути об'єктом, а отримано {evaluated.GetValueKind()}. Задайте аргументи полями або виразом, що повертає об'єкт."),
        };

        var properties = (servers.GetToolsCache(serverId)?.Find(toolName)?.InputSchema["properties"]) as JsonObject;
        var rawArgs = raw as JsonObject;
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, argument) in args)
        {
            var current = argument;
            if (properties?[name] is { } schema
                && rawArgs?[name] is JsonValue source
                && source.TryGetValue<string>(out var expression)
                && ExpressionSyntax.ContainsExpression(expression))
            {
                current = Coerce(expression, current?.DeepClone(), schema);
            }

            result[name] = current is null
                ? JsonDocument.Parse("null").RootElement.Clone()
                : JsonSerializer.SerializeToElement(current, WorkflowJson.Options);
        }

        return result;
    }

    private static JsonNode? Coerce(string expression, JsonNode? value, JsonNode schema)
    {
        try
        {
            return ExpressionResultCoercion.Coerce(expression, value, schema);
        }
        catch (ExpressionException ex)
        {
            throw new McpToolException($"Аргумент {expression}: {ex.Message.TrimEnd('.')}. Змініть вираз, щоб він повертав значення потрібного типу.", ex)
            {
                Details = ex.Message,
            };
        }
    }

    private static async Task<List<Item>> ToItemsAsync(INodeContext ctx, CallToolResult result, string? itemsPath, string toolName, CancellationToken ct)
    {
        if (result.StructuredContent is { } structured)
        {
            // Не-об'єкт (SDK віддає колекцію як голий масив) загортається у { "result": … } — так і в item, і для itemsPath.
            var json = JsonSerializer.SerializeToNode(structured, WorkflowJson.Options) as JsonObject
                ?? new JsonObject { ["result"] = JsonSerializer.SerializeToNode(structured, WorkflowJson.Options) };
            return itemsPath is null ? [new Item { Json = json }] : Expand(json, itemsPath, toolName);
        }

        if (itemsPath is not null)
        {
            throw new McpToolException(
                $"Дія «{toolName}» повернула відповідь без структури (structuredContent), тож шлях до записів «{itemsPath}» не застосувати. Очистьте поле itemsPath.");
        }

        var items = new List<Item>(result.Content.Count);
        foreach (var block in result.Content)
        {
            items.Add(await ToItemAsync(ctx, block, ct).ConfigureAwait(false));
        }

        return items;
    }

    /// <summary>
    /// <c>text</c> → <c>{ text }</c>; <c>image</c>/<c>audio</c>/<c>resource</c> → байти у сховище, посилання в
    /// <c>binary.data</c> (у item байтів немає); решта блоків — як є.
    /// </summary>
    private static async Task<Item> ToItemAsync(INodeContext ctx, ContentBlock block, CancellationToken ct)
    {
        switch (block)
        {
            case TextContentBlock text:
                return new Item { Json = new JsonObject { ["text"] = text.Text } };

            case ImageContentBlock image:
                return await BinaryItemAsync(ctx, "image", image.DecodedData, image.MimeType, null, null, ct).ConfigureAwait(false);

            case AudioContentBlock audio:
                return await BinaryItemAsync(ctx, "audio", audio.DecodedData, audio.MimeType, null, null, ct).ConfigureAwait(false);

            case EmbeddedResourceBlock { Resource: BlobResourceContents blob }:
                return await BinaryItemAsync(ctx, "resource", blob.DecodedData, blob.MimeType, blob.Uri, FileName(blob.Uri), ct).ConfigureAwait(false);

            case EmbeddedResourceBlock { Resource: TextResourceContents textResource }:
                return await BinaryItemAsync(
                    ctx, "resource", Encoding.UTF8.GetBytes(textResource.Text), textResource.MimeType ?? "text/plain",
                    textResource.Uri, FileName(textResource.Uri), ct).ConfigureAwait(false);

            default:
                var json = JsonSerializer.SerializeToNode(block, McpJsonUtilities.DefaultOptions) as JsonObject;
                return new Item { Json = json ?? new JsonObject { ["type"] = block.Type } };
        }
    }

    /// <summary>
    /// Розгортає масив за <paramref name="itemsPath"/> у <c>structuredContent</c> в item на елемент:
    /// об'єкт → <c>json</c> як є, інше (рядок, число, масив) → <c>{ "value": … }</c>.
    /// </summary>
    internal static List<Item> Expand(JsonObject structured, string itemsPath, string toolName)
    {
        var node = ResolvePath(structured, itemsPath);
        if (node is not JsonArray array)
        {
            throw new McpToolException(
                $"Дія «{toolName}»: за шляхом itemsPath «{itemsPath}» очікувався масив, а знайдено {(node is null ? "нічого" : node.GetValueKind().ToString())}. Вкажіть шлях до масиву у відповіді або очистьте itemsPath.");
        }

        return [.. array.Select(element => new Item
        {
            Json = element?.DeepClone() is JsonObject obj ? obj : new JsonObject { ["value"] = element?.DeepClone() },
        })];
    }

    /// <summary>Значення за шляхом через крапку (<c>order.id</c>, <c>lines.0.sku</c>); <c>null</c>, якщо його немає.</summary>
    public static JsonNode? ResolvePath(JsonNode? node, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            node = node switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
                _ => null,
            };

            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    private static async Task<Item> BinaryItemAsync(
        INodeContext ctx, string type, ReadOnlyMemory<byte> data, string? mimeType, string? uri, string? fileName, CancellationToken ct)
    {
        var mime = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        var reference = await ctx.Binary.PutAsync(stream, mime, fileName, ct).ConfigureAwait(false);

        var json = new JsonObject { ["type"] = type, ["mimeType"] = mime };
        if (uri is not null)
        {
            json["uri"] = uri;
        }

        return new Item { Json = json, Binary = new() { [McpToolNode.BinaryKey] = reference } };
    }

    private static string? FileName(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;
        var name = Path.GetFileName(path.TrimEnd('/'));
        return name.Length == 0 ? null : Uri.UnescapeDataString(name);
    }
}

/// <summary>Параметри виклику tool'а, спільні для <c>mcp.tool</c>, <c>mcp.poll</c> і <c>mcp.subscribe</c>.</summary>
internal class McpCallParameters
{
    public string ServerId { get; set; } = "";

    public string ToolName { get; set; } = "";

    public JsonNode? Arguments { get; set; }

    /// <summary>Шлях до масиву записів у <c>structuredContent</c>; <c>null</c> — вся відповідь один item.</summary>
    public string? ItemsPath { get; set; }
}
