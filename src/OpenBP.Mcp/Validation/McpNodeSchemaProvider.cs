using System.Text.Json.Nodes;
using OpenBP.Engine.Expressions;
using OpenBP.Engine.Validation;
using OpenBP.Mcp.Client;
using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Mcp.Validation;

/// <summary>
/// <see cref="INodeSchemaProvider"/> для вузлів <c>mcp.tool</c>, <c>mcp.poll</c> і <c>mcp.waitFor</c> (правила 5–6 з docs/workflow-format.md):
/// <c>serverId</c> зареєстровано, tool <c>toolName</c> є в кеші сервера (інакше <see cref="WorkflowValidationCodes.McpToolUnknown"/>),
/// <c>arguments</c> відповідають <c>inputSchema</c> tool'а (рядок <c>{{ }}</c> допустимий замість будь-якого типу).
/// Решту типів делегує внутрішньому провайдеру (зазвичай <see cref="NodeRegistrySchemaProvider"/>).
/// </summary>
public sealed class McpNodeSchemaProvider : INodeSchemaProvider
{
    /// <summary>Тип вузла виклику tool'а.</summary>
    public const string ToolNodeType = "mcp.tool";

    /// <summary>Тип polling-тригера.</summary>
    public const string PollNodeType = "mcp.poll";

    /// <summary>Тип вузла очікування умови (F5-03).</summary>
    public const string WaitForNodeType = "mcp.waitFor";

    /// <summary>Push-тригер подій: підписка на ресурс конектора (F6-13).</summary>
    public const string SubscribeNodeType = "mcp.subscribe";

    private static readonly NodePorts ToolPorts = new(1, 1);
    private static readonly NodePorts PollPorts = new(0, 1);
    private static readonly NodePorts WaitForPorts = new(1, 2);
    private static readonly NodePorts SubscribePorts = new(0, 1);

    private readonly IMcpServerRegistry _servers;
    private readonly INodeSchemaProvider? _inner;

    /// <summary>Створює провайдер.</summary>
    /// <param name="servers">Реєстр MCP-серверів із кешами tools.</param>
    /// <param name="inner">Провайдер для решти типів і базових параметрів <c>mcp.*</c>, коли їхні дескриптори зареєстровані.</param>
    public McpNodeSchemaProvider(IMcpServerRegistry servers, INodeSchemaProvider? inner = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _servers = servers;
        _inner = inner;
    }

    /// <inheritdoc />
    public NodePorts? GetPorts(string type, int typeVersion) =>
        _inner?.GetPorts(type, typeVersion) ?? type switch
        {
            ToolNodeType => ToolPorts,
            PollNodeType => PollPorts,
            WaitForNodeType => WaitForPorts,
            SubscribeNodeType => SubscribePorts,
            _ => null,
        };

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowValidationError>> ValidateNodeAsync(NodeDefinition node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Type is not (ToolNodeType or PollNodeType or WaitForNodeType or SubscribeNodeType))
        {
            return _inner is null ? [] : await _inner.ValidateNodeAsync(node, ct).ConfigureAwait(false);
        }

        var errors = new List<WorkflowValidationError>();
        var described = _inner?.GetPorts(node.Type, node.TypeVersion) is not null;
        if (described)
        {
            // Дескриптор mcp.* зареєстровано — його схема перевіряє решту параметрів (cron, keyField, …).
            errors.AddRange(await _inner!.ValidateNodeAsync(node, ct).ConfigureAwait(false));
        }

        var serverId = RequiredString(node, "serverId", described, errors);
        if (node.Type == SubscribeNodeType)
        {
            // Ресурс обов'язковий, tool — необов'язковий (без нього читається сам ресурс).
            RequiredString(node, "resourceUri", described, errors);
            RequiredString(node, "keyField", described, errors);
            if (node.Parameters["toolName"] is not JsonValue optional || !optional.TryGetValue<string>(out var optionalTool) || optionalTool.Length == 0)
            {
                return errors;
            }
        }

        var toolName = RequiredString(node, "toolName", described, errors);
        if (serverId is null || toolName is null
            || ExpressionSyntax.ContainsExpression(serverId) || ExpressionSyntax.ContainsExpression(toolName))
        {
            // Сервер чи tool визначаються лише під час виконання — статично перевірити нічого.
            return errors;
        }

        if (!_servers.TryGetServer(serverId, out var server))
        {
            errors.Add(new(WorkflowValidationCodes.McpToolUnknown,
                $"MCP-сервер «{serverId}» не зареєстровано.", node.Id, "/parameters/serverId"));
            return errors;
        }

        var cache = _servers.GetToolsCache(serverId);
        var tool = cache?.Find(toolName);
        if (tool is null)
        {
            errors.Add(new(WorkflowValidationCodes.McpToolUnknown,
                cache is null
                    ? $"Список tools сервера «{server.Name}» ще не отримано — оновіть його, щоб перевірити «{toolName}»."
                    : $"Tool «{toolName}» не знайдено на сервері «{server.Name}».",
                node.Id, "/parameters/toolName"));
            return errors;
        }

        var arguments = node.Parameters["arguments"];
        if (arguments is JsonValue value && value.TryGetValue<string>(out var text) && ExpressionSyntax.ContainsExpression(text))
        {
            return errors;
        }

        errors.AddRange(ParameterSchemaValidator.Validate(tool.InputSchema, arguments ?? new JsonObject(), node.Id, "/parameters/arguments"));
        return errors;
    }

    /// <summary>
    /// Рядковий параметр; якщо його немає і дескриптор не зареєстровано (тобто ніхто інший про це не скаже) — помилка.
    /// </summary>
    private static string? RequiredString(NodeDefinition node, string name, bool described, List<WorkflowValidationError> errors)
    {
        if (node.Parameters[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
        {
            return text;
        }

        if (!described)
        {
            errors.Add(new(WorkflowValidationCodes.ParameterInvalid,
                $"/parameters/{name}: обов'язковий непорожній рядок.", node.Id, $"/parameters/{name}"));
        }

        return null;
    }
}
