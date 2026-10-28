using System.Text.Json;
using OpenBP.Application.Connectors;
using OpenBP.Application.Paging;
using OpenBP.Engine.Nodes;

namespace OpenBP.Application.Nodes;

/// <summary>Вузол у стислому каталозі: достатньо, щоб модель обрала тип, без схеми параметрів.</summary>
/// <param name="Type">Тип вузла — <c>type</c> у сценарії.</param>
/// <param name="Version">Остання версія типу — <c>typeVersion</c>.</param>
/// <param name="Title">Назва в палітрі.</param>
/// <param name="Summary">Один рядок опису.</param>
/// <param name="Kind">Роль у плані: <c>trigger|action|condition|approval|wait|transform|notify</c>.</param>
/// <param name="Outputs">Назви вихідних портів (індекс у списку — <c>from.output</c>).</param>
public sealed record DigestNode(
    string Type,
    int Version,
    string Title,
    string Summary,
    string Kind,
    IReadOnlyList<string> Outputs);

/// <summary>Дія підключеної системи в стислому каталозі: tool MCP-конектора для вузла <c>mcp.tool</c>.</summary>
/// <param name="Use">Ідентифікатор дії <c>mcp:&lt;serverId&gt;/&lt;toolName&gt;</c>.</param>
/// <param name="Connector">Назва конектора.</param>
/// <param name="Title">Назва tool'а.</param>
/// <param name="Summary">Один рядок опису.</param>
public sealed record DigestAction(string Use, string Connector, string Title, string Summary);

/// <summary>Стислий каталог для малих моделей: типи вузлів і дії конекторів без схем (docs/workflow-drafter.md).</summary>
/// <param name="Nodes">Остання версія кожного типу вузла.</param>
/// <param name="Actions">Tools конекторів у стані «підключено».</param>
/// <param name="Truncated"><c>true</c>, якщо дії не вмістилися в бюджет і частину відкинуто.</param>
public sealed record CatalogDigest(IReadOnlyList<DigestNode> Nodes, IReadOnlyList<DigestAction> Actions, bool Truncated = false);

/// <summary>Збирає <see cref="CatalogDigest"/> з тих самих сервісів, що REST і MCP (права — viewer).</summary>
public interface ICatalogDigest
{
    /// <summary>Максимальний розмір JSON каталогу в символах.</summary>
    public const int MaxChars = 6000;

    /// <summary>Будує каталог.</summary>
    /// <param name="cancellationToken">Скасування.</param>
    Task<CatalogDigest> BuildAsync(CancellationToken cancellationToken = default);
}

/// <summary><see cref="ICatalogDigest"/> поверх <see cref="INodeService"/> і <see cref="IConnectorService"/>.</summary>
/// <param name="nodes">Сервіс вузлів.</param>
/// <param name="connectors">Сервіс конекторів.</param>
public sealed class CatalogDigestBuilder(INodeService nodes, IConnectorService connectors) : ICatalogDigest
{
    private static readonly JsonSerializerOptions SizeOptions = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, string> KindByType = new(StringComparer.Ordinal)
    {
        ["core.if"] = "condition",
        ["core.switch"] = "condition",
        ["core.decisionTable"] = "condition",
        ["core.approval"] = "approval",
        ["core.wait"] = "wait",
        ["mcp.waitFor"] = "wait",
        ["core.set"] = "transform",
        ["core.code"] = "transform",
        ["core.merge"] = "transform",
        ["core.loop"] = "transform",
        ["ai.llm"] = "transform",
        ["ai.agent"] = "transform",
        ["core.respondToWebhook"] = "notify",
    };

    /// <inheritdoc />
    public async Task<CatalogDigest> BuildAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var latest = descriptors
            .GroupBy(d => d.Type, StringComparer.Ordinal)
            .Select(g => g.MaxBy(d => d.Version)!)
            .ToList();

        var actions = new List<DigestAction>();
        var page = await connectors.ListAsync(new PageRequest { Page = 1, PageSize = PageRequest.MaxPageSize }, cancellationToken).ConfigureAwait(false);
        foreach (var summary in page.Items.Where(c => c.Status == ConnectorStatus.Ok))
        {
            var detail = await connectors.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            actions.AddRange(detail.Tools.Select(t => new DigestAction(
                $"mcp:{summary.Id}/{t.Name}", summary.Name, t.Title ?? t.Name, FirstLine(t.Description, int.MaxValue))));
        }

        return Fit(latest, actions);
    }

    private static CatalogDigest Fit(List<NodeDescriptor> descriptors, List<DigestAction> actions)
    {
        // Спершу скорочуємо описи, потім відкидаємо зайві дії; вузли лишаються всі.
        foreach (var limit in new[] { 100, 60, 30 })
        {
            var digest = Make(descriptors, actions, limit);
            if (Size(digest) <= ICatalogDigest.MaxChars)
            {
                return digest;
            }
        }

        var kept = actions.ToList();
        var shrunk = Make(descriptors, kept, 30);
        while (kept.Count > 0 && Size(shrunk) > ICatalogDigest.MaxChars)
        {
            kept.RemoveAt(kept.Count - 1);
            shrunk = Make(descriptors, kept, 30) with { Truncated = true };
        }

        return shrunk;
    }

    private static CatalogDigest Make(List<NodeDescriptor> descriptors, List<DigestAction> actions, int summaryLimit) => new(
        [
            .. descriptors
                .OrderBy(d => d.Group)
                .ThenBy(d => d.Type, StringComparer.Ordinal)
                .Select(d => new DigestNode(
                    d.Type, d.Version, d.DisplayName, FirstLine(d.Description, summaryLimit), KindOf(d), d.Outputs)),
        ],
        [.. actions.Select(a => a with { Summary = FirstLine(a.Summary, summaryLimit) })]);

    private static int Size(CatalogDigest digest) => JsonSerializer.Serialize(digest, SizeOptions).Length;

    private static string KindOf(NodeDescriptor d) =>
        d.PlanKind ?? (d.IsTrigger ? "trigger" : KindByType.GetValueOrDefault(d.Type, "action"));

    private static string FirstLine(string? text, int limit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Trim().Split('\n', 2)[0].Trim();
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0)
        {
            line = line[..(dot + 1)];
        }

        return line.Length <= limit ? line : line[..(limit - 1)].TrimEnd() + "…";
    }
}
