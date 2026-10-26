using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OpenBP.Engine.Nodes;
using OpenBP.Mcp.Client;
using OpenBP.Mcp.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Nodes;

/// <summary>
/// <c>mcp.subscribe</c> — push-тригер подій (docs/connectors.md §9, F6-13). Підписку на ресурс <c>resourceUri</c>
/// тримає воркер (<c>SubscriptionService</c>); сповіщення <c>notifications/resources/updated</c> — лише сигнал
/// виконати цей вузол. Сам вузол читає події так само, як <c>mcp.poll</c>: tool'ом <c>toolName</c> (з <c>arguments</c>,
/// <c>itemsPath</c>), а без нього — самим ресурсом (<c>resources/read</c>, текст JSON; масив загортається у
/// <c>{ "result": [...] }</c>, <c>itemsPath</c> розгортає список) — і віддає лише записи з новими ключами
/// (<see cref="SeenKeys"/>). Тому пропущене сповіщення не губить подію: наступне читання віддасть усе, чого ще не бачили.
/// </summary>
public sealed class McpSubscribeNode : INode
{
    private readonly IMcpServerRegistry _servers;
    private readonly McpToolInvoker _invoker;

    /// <summary>Створює вузол.</summary>
    /// <param name="servers">Реєстр MCP-серверів із пулом сесій.</param>
    public McpSubscribeNode(IMcpServerRegistry servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _servers = servers;
        _invoker = new McpToolInvoker(servers);
    }

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: McpNodeSchemaProvider.SubscribeNodeType,
        Version: 1,
        DisplayName: "Подія конектора",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: McpToolNode.LoadSchema("subscribe"),
        Description: "Запускається, коли конектор сповіщає про нову подію; віддає лише нові записи.");

    /// <inheritdoc />
    public async Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var p = ctx.Params<SubscribeParameters>(0);
        if (string.IsNullOrWhiteSpace(p.KeyField))
        {
            throw new McpToolException("Не задано ключове поле (keyField), за яким розпізнаються нові події. Вкажіть його у вузлі.");
        }

        if (string.IsNullOrWhiteSpace(p.ServerId) || string.IsNullOrWhiteSpace(p.ResourceUri))
        {
            throw new McpToolException("У вузлі не обрано конектор або ресурс подій. Відкрийте вузол і оберіть їх.");
        }

        var items = string.IsNullOrWhiteSpace(p.ToolName)
            ? await ReadResourceAsync(ctx, p, ct).ConfigureAwait(false)
            : await _invoker.CallAsync(ctx, p, ct).ConfigureAwait(false);
        return [[.. SeenKeys.Filter(ctx, items, p.KeyField)]];
    }

    /// <summary><c>resources/read</c>: усі текстові JSON-вмісти ресурсу → items (як <c>structuredContent</c> у tool'а).</summary>
    private async Task<List<Item>> ReadResourceAsync(INodeContext ctx, SubscribeParameters p, CancellationToken ct)
    {
        var serverName = _servers.TryGetServer(p.ServerId, out var config) ? config.Name : p.ServerId;
        var timeout = ctx.Node.TimeoutSec is > 0 and var seconds ? TimeSpan.FromSeconds(seconds) : McpToolNode.DefaultTimeout;
        ReadResourceResult result;
        try
        {
            result = await _servers.RunAsync(
                p.ServerId,
                (client, token) => client.ReadResourceAsync(p.ResourceUri, cancellationToken: token).AsTask(),
                timeout,
                ct).ConfigureAwait(false);
        }
        catch (McpProtocolException ex)
        {
            throw new McpToolException(
                $"Конектор «{serverName}» не віддав ресурс «{p.ResourceUri}»: {FirstLine(ex.Message)}. Перевірте адресу ресурсу у вузлі.", ex)
            {
                Details = ex.Message,
            };
        }
        catch (McpUnavailableException ex)
        {
            throw new McpToolException(
                $"{ex.Message.TrimEnd('.')}. Перевірте конектор у розділі «Конектори» і повторіть запуск.", ex)
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

        var items = new List<Item>();
        var itemsPath = string.IsNullOrWhiteSpace(p.ItemsPath) ? null : p.ItemsPath;
        foreach (var contents in result.Contents)
        {
            if (contents is not TextResourceContents text)
            {
                throw new McpToolException(
                    $"Ресурс «{p.ResourceUri}» конектора «{serverName}» не текстовий. Для подій ресурс має віддавати JSON.");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text.Text);
            }
            catch (JsonException ex)
            {
                throw new McpToolException(
                    $"Ресурс «{p.ResourceUri}» конектора «{serverName}» віддав не JSON: {FirstLine(ex.Message)}. Для подій ресурс має віддавати JSON.", ex)
                {
                    Details = ex.Message,
                };
            }

            var json = parsed as JsonObject ?? new JsonObject { ["result"] = parsed };
            if (itemsPath is null)
            {
                items.Add(new Item { Json = json });
            }
            else
            {
                items.AddRange(McpToolInvoker.Expand(json, itemsPath, p.ResourceUri));
            }
        }

        return items;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line.TrimEnd('.');
    }

    private sealed class SubscribeParameters : McpCallParameters
    {
        public string ResourceUri { get; set; } = "";

        public string KeyField { get; set; } = "";
    }
}
