using System.Text.Json.Nodes;
using OpenBP.Ai;

namespace OpenBP.Application.Assistant;

/// <summary>Інструменти помічника — ті самі, що tools <c>/mcp</c> (docs/mcp-server.md), але над сценарієм із редактора.</summary>
internal static class AssistantTools
{
    public const string DescribeNodes = "describe_nodes";
    public const string DescribeNode = "describe_node";
    public const string DescribeConnectors = "describe_connectors";
    public const string GetWorkflow = "get_workflow";
    public const string UpdateWorkflow = "update_workflow";
    public const string TestNode = "test_node";

    private static JsonObject Empty() => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public static IReadOnlyList<LlmTool> All { get; } =
    [
        new(
            DescribeNodes,
            "Short catalogue of node types: `type`/`version` (use as node `type`/`typeVersion`), title, one-line summary, kind and " +
            "output port names (connection indexes), plus the actions of connected systems (`use` = mcp:<serverId>/<toolName>). " +
            "No parameter schemas: call describe_node for each node type you are going to add. Call it before adding nodes.",
            Empty()),
        new(
            DescribeNode,
            "Returns one node type in full: ports, credentials and the JSON Schema of `parameters`. `version` defaults to the latest.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["type"] = new JsonObject { ["type"] = "string", ["description"] = "Node type, e.g. core.if." },
                    ["version"] = new JsonObject { ["type"] = "integer" },
                },
                ["required"] = new JsonArray("type"),
            }),
        new(
            DescribeConnectors,
            "Lists MCP connectors (external systems) with their tools and input schemas. An `mcp.tool` node calls one: " +
            "parameters `serverId` (connector id), `toolName`, `arguments`.",
            Empty()),
        new(GetWorkflow, "Returns the current workflow definition from the editor (it may contain unsaved changes).", Empty()),
        new(
            UpdateWorkflow,
            "Replaces the workflow definition in the editor with `definition` (the full document: formatVersion, name, settings, " +
            "nodes, connections, pinData). Returns validation errors; fix them with another update_workflow. The user can undo it.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["definition"] = new JsonObject { ["type"] = "object", ["description"] = "Full workflow definition." },
                },
                ["required"] = new JsonArray("definition"),
            }),
        new(
            TestNode,
            "Executes one node of the last SAVED version of the workflow and returns its output items (first 5 per port) " +
            "or its error. With `items`, they are the node's input 0.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["nodeId"] = new JsonObject { ["type"] = "string" },
                    ["items"] = new JsonObject { ["type"] = "array", ["description"] = "Input items, e.g. [{\"json\": {...}}]." },
                },
                ["required"] = new JsonArray("nodeId"),
            }),
    ];
}

/// <summary>Системна інструкція помічника.</summary>
internal static class AssistantPrompt
{
    public const string System =
        """
        You are the assistant inside the OpenBP workflow editor. You help a business user build and fix one workflow.
        Reply in the user's language (usually Ukrainian), briefly, without technical jargon unless asked.

        A workflow is JSON: { formatVersion: 1, name, settings, nodes: [{ id, name, type, typeVersion, position: [x, y], parameters,
        disabled?, continueOnFail? }], connections: [{ from: { node, output }, to: { node, input } }], pinData }.
        Node ids are unique strings like "n_order"; names are unique, human-readable, in the user's language. Positions go left to
        right on a grid of 240 px horizontally and 120 px vertically, starting at [0, 0]. Every workflow starts with exactly one
        trigger node. `output`/`input` are port indexes from describe_nodes. Parameter values may be expressions "{{ … }}" with
        $json (current item), $node["Name"].json (an earlier node's item), $vars.key.

        To change the workflow, call get_workflow, then describe_nodes (and describe_connectors for external systems), then describe_node for
        every node type you add, then
        update_workflow with the FULL new definition — keep the nodes and connections you do not change exactly as they are.
        Integrations with external systems go only through `mcp.tool` with a connector from describe_connectors; never invent
        serverId values — leave serverId empty if there is no suitable connector and say so. Never ask for or write secrets
        (API keys, passwords) into parameters: credentials are chosen by the user in the node panel.
        When asked to explain, answer without changing the workflow. When you are done, say in one or two sentences what changed.
        """;
}
