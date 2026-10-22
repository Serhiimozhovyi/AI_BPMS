using System.Globalization;
using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// <c>mcp.poll</c> — polling-тригер (docs/connectors.md §3): викликає tool (результат → items так само, як у
/// <c>mcp.tool</c>), бере ключ кожного item'а за шляхом <c>keyField</c> у <c>json</c> і віддає лише items з ключами,
/// яких ще немає в <c>NodeState.seenKeys</c>. <c>seenKeys</c> — кільцевий буфер останніх <see cref="SeenKeysCapacity"/>
/// ключів; движок зберігає стан після успішного запуску. Розклад (<c>cron</c>) реєструє хост, вузол його не читає.
/// </summary>
public sealed class McpPollNode : INode
{
    /// <summary>Ємність кільцевого буфера <c>seenKeys</c>.</summary>
    public const int SeenKeysCapacity = 1000;

    /// <summary>Ключ стану вузла з уже баченими ключами.</summary>
    public const string SeenKeysState = "seenKeys";

    private readonly McpToolInvoker _invoker;

    /// <summary>Створює вузол.</summary>
    /// <param name="servers">Реєстр MCP-серверів із пулом сесій.</param>
    public McpPollNode(IMcpServerRegistry servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _invoker = new McpToolInvoker(servers);
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: McpNodeSchemaProvider.PollNodeType,
        Version: 1,
        DisplayName: "MCP: опитування",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: McpToolNode.LoadSchema("poll"),
        Description: "Викликає tool MCP-сервера за розкладом і віддає лише нові записи.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var p = ctx.Params<PollParameters>(0);
        if (string.IsNullOrWhiteSpace(p.KeyField))
        {
            throw new McpToolException($"Вузол «{ctx.Node.Name}»: параметр keyField обов'язковий.");
        }

        var items = await _invoker.CallAsync(ctx, p, ct).ConfigureAwait(false);

        var seen = new List<JsonNode?>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (ctx.NodeState[SeenKeysState] is JsonArray stored)
        {
            foreach (var key in stored)
            {
                seen.Add(key?.DeepClone());
                known.Add(KeyText(key));
            }
        }

        var fresh = new List<Item>();
        foreach (var item in items)
        {
            var key = Resolve(item.Json, p.KeyField)
                ?? throw new McpToolException(
                    $"Вузол «{ctx.Node.Name}»: у записі немає ключового поля «{p.KeyField}» (або воно null).");
            if (known.Add(KeyText(key)))
            {
                seen.Add(key.DeepClone());
                fresh.Add(item);
            }
        }

        if (seen.Count > SeenKeysCapacity)
        {
            seen.RemoveRange(0, seen.Count - SeenKeysCapacity);
        }

        var state = (JsonObject)ctx.NodeState.DeepClone();
        state[SeenKeysState] = new JsonArray([.. seen]);
        ctx.NodeState = state;

        return [[.. fresh]];
    }

    /// <summary>Значення за шляхом через крапку (<c>order.id</c>, <c>lines.0.sku</c>); <c>null</c>, якщо його немає.</summary>
    private static JsonNode? Resolve(JsonNode? node, string path)
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

    /// <summary>Канонічний текст ключа: <c>1</c> і <c>"1"</c> — різні ключі.</summary>
    private static string KeyText(JsonNode? key) => key?.ToJsonString() ?? "null";

    private sealed class PollParameters : McpCallParameters
    {
        public string? Cron { get; set; }

        public string KeyField { get; set; } = "";
    }
}
