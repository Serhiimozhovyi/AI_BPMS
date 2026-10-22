using System.Reflection;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// <c>mcp.tool</c> — виклик tool'а MCP-сервера для кожного item'а (docs/connectors.md §2).
/// Аргументи — обчислені <c>arguments</c> (поля-вирази приводяться до типів з <c>inputSchema</c>);
/// результат: <c>structuredContent</c> → один item, інакше по item на <c>content</c>-блок.
/// <c>isError: true</c> → <see cref="McpToolException"/>; недоступність і таймаут (<c>timeoutSec</c>, за замовчуванням
/// <see cref="DefaultTimeout"/>) → <see cref="McpUnavailableException"/>.
/// </summary>
public sealed class McpToolNode : INode
{
    /// <summary>Таймаут виклику, якщо у вузла не задано <c>timeoutSec</c> (docs/workflow-format.md).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Ключ <see cref="Item.Binary"/>, під яким лежить вміст <c>image</c>/<c>audio</c>/<c>resource</c>-блоку.</summary>
    public const string BinaryKey = "data";

    private readonly McpToolInvoker _invoker;

    /// <summary>Створює вузол.</summary>
    /// <param name="servers">Реєстр MCP-серверів із пулом сесій.</param>
    public McpToolNode(IMcpServerRegistry servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _invoker = new McpToolInvoker(servers);
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: McpNodeSchemaProvider.ToolNodeType,
        Version: 1,
        DisplayName: "MCP: виклик tool'а",
        Group: NodeGroup.Integration,
        Inputs: ["main"],
        Outputs: ["main"],
        ParametersSchema: LoadSchema("tool"),
        Description: "Викликає tool підключеного MCP-сервера для кожного item'а.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var output = new List<Item>();
        var count = ctx.Items().Count;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var p = ctx.Params<McpCallParameters>(i);
            output.AddRange(await _invoker.CallAsync(ctx, p, ct).ConfigureAwait(false));
        }

        return [[.. output]];
    }

    /// <summary>Читає вбудовану схему параметрів <c>Schemas/&lt;name&gt;.schema.json</c>.</summary>
    internal static JsonObject LoadSchema(string name)
    {
        var resource = $"OpenBP.Mcp.Schemas.{name}.schema.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Ресурс {resource} не знайдено.");
        return JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException($"Ресурс {resource} не є JSON-об'єктом.");
    }
}
