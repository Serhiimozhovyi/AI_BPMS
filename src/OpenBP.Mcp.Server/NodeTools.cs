using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using OpenBP.Application.Errors;
using OpenBP.Application.Nodes;
using OpenBP.Engine.Nodes;

namespace OpenBP.Mcp.Server;

/// <summary>Тип вузла, як його бачить агент: що вписати в <c>type</c>/<c>typeVersion</c> і які <c>parameters</c> він приймає.</summary>
/// <param name="Type">Тип вузла — <c>type</c> у сценарії.</param>
/// <param name="Version">Версія — <c>typeVersion</c>.</param>
/// <param name="DisplayName">Назва в палітрі.</param>
/// <param name="Group">Група в палітрі.</param>
/// <param name="Description">Опис.</param>
/// <param name="Trigger">Тригер (без входів): з нього починається сценарій.</param>
/// <param name="Inputs">Назви вхідних портів (<c>to.input</c> — індекс у цьому списку).</param>
/// <param name="Outputs">Назви вихідних портів (<c>from.output</c> — індекс; <c>continueOnFail</c> додає останній <c>error</c>).</param>
/// <param name="ParametersSchema">JSON Schema <c>parameters</c>; рядок <c>{{ }}</c> допустимий замість будь-якого значення.</param>
/// <param name="Credentials">Параметри з id credential'а, які вузол запитує.</param>
public sealed record NodeTypeDescription(
    string Type,
    int Version,
    string DisplayName,
    NodeGroup Group,
    string? Description,
    bool Trigger,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    JsonObject ParametersSchema,
    IReadOnlyList<NodeCredentialDescription> Credentials);

/// <summary>Credential, якого потребує вузол.</summary>
/// <param name="Parameter">Параметр вузла з id credential'а.</param>
/// <param name="Type">Тип credential'а.</param>
/// <param name="Required">Чи обов'язковий.</param>
public sealed record NodeCredentialDescription(string Parameter, string Type, bool Required);

/// <summary>Результат <c>describe_nodes</c>.</summary>
/// <param name="Nodes">Зареєстровані типи вузлів за групою і типом; порожній у стислому режимі.</param>
/// <param name="Compact">Стислий каталог (<c>compact: true</c>): вузли й дії конекторів без схем; інакше <c>null</c>.</param>
public sealed record NodesDescription(IReadOnlyList<NodeTypeDescription> Nodes, CatalogDigest? Compact = null)
{
    /// <summary>Стислий каталог без схем параметрів (F3-102): вузли й дії підключених конекторів.</summary>
    /// <param name="digest">Сервіс стислого каталогу.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<NodesDescription> BuildCompactAsync(ICatalogDigest digest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(digest);
        return new NodesDescription([], await digest.BuildAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Повний опис однієї версії типу (остання, якщо <paramref name="version"/> не задано).</summary>
    /// <param name="nodes">Сервіс вузлів.</param>
    /// <param name="type">Тип вузла.</param>
    /// <param name="version">Версія.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Такого типу чи версії немає.</exception>
    public static async Task<NodeTypeDescription> BuildOneAsync(INodeService nodes, string type, int? version, CancellationToken ct)
    {
        var all = (await BuildAsync(nodes, ct).ConfigureAwait(false)).Nodes.Where(n => n.Type == type).ToList();
        var found = version is null ? all.MaxBy(n => n.Version) : all.FirstOrDefault(n => n.Version == version);
        return found ?? throw new NotFoundException("node", version is null ? type : $"{type}@{version}");
    }

    /// <summary>Збирає опис з <see cref="INodeService"/> (права — viewer), як REST <c>GET /nodes</c>.</summary>
    /// <param name="nodes">Сервіс вузлів.</param>
    /// <param name="ct">Скасування.</param>
    public static async Task<NodesDescription> BuildAsync(INodeService nodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var descriptors = await nodes.ListAsync(ct).ConfigureAwait(false);
        return new NodesDescription(
        [
            .. descriptors.Select(d => new NodeTypeDescription(
                d.Type,
                d.Version,
                d.DisplayName,
                d.Group,
                d.Description,
                d.IsTrigger,
                d.Inputs,
                d.Outputs,
                d.ParametersSchema,
                [.. (d.Credentials ?? []).Select(c => new NodeCredentialDescription(c.Parameter, c.Type, c.Required))])),
        ]);
    }
}

/// <summary>Tools типів вузлів (ADR-002).</summary>
[McpServerToolType]
public sealed class NodeTools
{
    /// <summary><c>describe_nodes</c>: зареєстровані типи вузлів з параметрами.</summary>
    /// <param name="nodes">Сервіс вузлів.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "describe_nodes", Title = "Describe node types", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Lists the node types AI_BPMS can run: `type` and `version` (use as `type`/`typeVersion`), whether it is a trigger, " +
        "input/output port names (connection indexes) and the JSON Schema of `parameters`. Any parameter value may be an " +
        "expression string `{{ … }}` instead: `$json` is the current input item from the previous node, " +
        "`$node[\"Node name\"].json` the matching item of an earlier node, `$binary` the current item's files " +
        "(references with fileName, mimeType, fileSize; `{}` when there are none). " +
        "`mcp.tool` returns a tool's structured result as one item; set `itemsPath` to split an array into items. " +
        "`mcp.waitFor` calls a tool every `intervalMinutes` until `conditions` (`field` is a dot path into the tool's result, " +
        "operators as in `core.if`) hold, or `timeoutHours` passes: output 0 `done` carries the last result, output 1 `timeout` " +
        "the input item; between checks the execution is in state waiting. " +
        "`core.executeWorkflow` runs another workflow that starts with `core.workflowTrigger` (optional `inputSchema` for each item's json; " +
        "such a workflow cannot be activated alone): `items` is an expression per input item (array → one item per element; empty → the input " +
        "items), `mode: wait` outputs the child's last successful node output (a failed child is a node error), `mode: fireAndForget` outputs " +
        "{ executionId } at once; calls must not form a cycle and nest at most 8 deep; get_execution lists `children`. " +
        "Tools of connected systems are listed by describe_connectors. " +
        "In `core.code` JavaScript the same `$node[\"Node name\"].json` is available (plus `.all` with every item), " +
        "and `Date` uses the workflow's `settings.timezone`. " +
        "Any node may carry optional `business` fields for the business (swimlane) view, not executed by the engine: " +
        "`title` and `subtitle` shown instead of `name`, `lane` (id from the workflow-level `business.lanes`) and `hidden` " +
        "(true hides the node; false shows a core.set/core.code/core.merge node that is otherwise collapsed). " +
        "At the workflow level `business.lanes` lists participants ({ id, name, description? }) and `business.steps` the human " +
        "actions in external systems ({ id, title, subtitle?, lane, links: [{ node, direction: in|out, label? }] }); " +
        "a connection may carry `business.label` for its arrow. " +
        "A business process may also declare `stages` at the workflow level (ADR-004): { id, name, nodes: [node ids], " +
        "owner?: { lane | role | expression }, due?: { amount, unit: minutes|hours|days|businessDays } | { until }, " +
        "transitions?: [{ to?: stage id, rules: [{ id, condition, mode: block|warn, message? }] }] }. A node belongs to at most " +
        "one stage and a stage's nodes must be connected; the engine derives stage state from the execution and checks " +
        "transition rules on edges leaving the stage. See the format schema for details.")]
    public static Task<NodesDescription> DescribeNodes(
        INodeService nodes,
        ICatalogDigest digest,
        [Description("true: a short catalogue instead of full schemas — node types (type, version, title, one-line summary, kind, outputs) and actions of connected systems (`use` = mcp:<serverId>/<toolName>), up to ~6000 characters. Fetch one node's schema with describe_node. Default false.")] bool compact = false,
        CancellationToken ct = default) =>
        compact ? NodesDescription.BuildCompactAsync(digest, ct) : NodesDescription.BuildAsync(nodes, ct);

    /// <summary><c>describe_node</c>: один тип вузла з повною схемою параметрів.</summary>
    /// <param name="nodes">Сервіс вузлів.</param>
    /// <param name="type">Тип вузла.</param>
    /// <param name="version">Версія; типово остання.</param>
    /// <param name="ct">Скасування.</param>
    [McpServerTool(Name = "describe_node", Title = "Describe one node type", ReadOnly = true, UseStructuredContent = true)]
    [Description(
        "Returns one node type in full: ports, credentials and the JSON Schema of `parameters` (the same entry as in describe_nodes). " +
        "Use it after describe_nodes with compact=true. `version` defaults to the latest. Unknown type: NOT_FOUND.")]
    public static Task<NodeTypeDescription> DescribeNode(
        INodeService nodes,
        [Description("Node type, e.g. core.if.")] string type,
        [Description("Node type version; the latest when omitted.")] int? version = null,
        CancellationToken ct = default) =>
        NodesDescription.BuildOneAsync(nodes, type, version, ct);
}
