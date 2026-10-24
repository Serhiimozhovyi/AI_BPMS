using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
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
/// <param name="Nodes">Зареєстровані типи вузлів за групою і типом.</param>
public sealed record NodesDescription(IReadOnlyList<NodeTypeDescription> Nodes)
{
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
        "Lists the node types OpenBP can run: `type` and `version` (use as `type`/`typeVersion`), whether it is a trigger, " +
        "input/output port names (connection indexes) and the JSON Schema of `parameters`. Any parameter value may be an " +
        "expression string `{{ … }}` instead: `$json` is the current input item from the previous node, " +
        "`$node[\"Node name\"].json` the matching item of an earlier node. " +
        "`mcp.tool` returns a tool's structured result as one item; set `itemsPath` to split an array into items. " +
        "Tools of connected systems are listed by describe_connectors. " +
        "In `core.code` JavaScript the same `$node[\"Node name\"].json` is available (plus `.all` with every item), " +
        "and `Date` uses the workflow's `settings.timezone`.")]
    public static Task<NodesDescription> DescribeNodes(INodeService nodes, CancellationToken ct) =>
        NodesDescription.BuildAsync(nodes, ct);
}
