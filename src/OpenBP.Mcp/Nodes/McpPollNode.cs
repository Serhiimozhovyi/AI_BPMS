using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// <c>mcp.poll</c> — polling-тригер (docs/connectors.md §3): викликає tool (результат → items так само, як у
/// <c>mcp.tool</c>, зокрема <c>itemsPath</c> розгортає список записів зі <c>structuredContent</c>), бере ключ кожного item'а за шляхом <c>keyField</c> у <c>json</c> і віддає лише items з ключами,
/// яких ще немає в <c>NodeState.seenKeys</c>. <c>seenKeys</c> — кільцевий буфер останніх <see cref="SeenKeysCapacity"/>
/// ключів; движок зберігає стан після успішного запуску. Розклад (<c>cron</c>) реєструє хост, вузол його не читає.
/// </summary>
public sealed class McpPollNode : INode
{
    /// <summary>Ємність кільцевого буфера <c>seenKeys</c>.</summary>
    public const int SeenKeysCapacity = SeenKeys.Capacity;

    /// <summary>Ключ стану вузла з уже баченими ключами.</summary>
    public const string SeenKeysState = SeenKeys.StateKey;

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
            throw new McpToolException("Не задано ключове поле (keyField), за яким розпізнаються нові записи. Вкажіть його у вузлі.");
        }

        var items = await _invoker.CallAsync(ctx, p, ct).ConfigureAwait(false);
        return [[.. SeenKeys.Filter(ctx, items, p.KeyField)]];
    }

    private sealed class PollParameters : McpCallParameters
    {
        public string? Cron { get; set; }

        public string KeyField { get; set; } = "";
    }
}
