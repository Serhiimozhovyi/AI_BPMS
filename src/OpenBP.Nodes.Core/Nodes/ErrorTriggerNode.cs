using System.Text.Json.Nodes;
using OpenBP.Engine.Nodes;
using OpenBP.Shared.Workflow;

namespace OpenBP.Nodes.Core;

/// <summary>
/// <c>core.errorTrigger</c> — старт сценарію помилок (F4-06). Коли виконання сценарію з <c>settings.errorWorkflowId</c>
/// завершилось <c>error</c> або <c>timed_out</c>, воркер запускає сценарій помилок у режимі <c>error</c> з одним item
/// <c>{ execution: { id, status, startedAt, finishedAt, mode }, workflow: { id, name }, error: { message, nodeId, nodeName } }</c>
/// на виході цього вузла (без значень credentials). Сам вузол виконується лише в ручному тесті без <c>pinData</c> —
/// тоді віддає приклад такого item.
/// </summary>
public sealed class ErrorTriggerNode : INode
{
    /// <summary>Тип вузла.</summary>
    public const string Type = "core.errorTrigger";

    /// <inheritdoc />
    public NodeDescriptor Descriptor { get; } = new(
        Type: Type,
        Version: 1,
        DisplayName: "Помилка сценарію",
        Group: NodeGroup.Trigger,
        Inputs: [],
        Outputs: ["main"],
        ParametersSchema: NodeSchemas.Load("errorTrigger"),
        Description: "Запускає сценарій, коли інший сценарій (у його налаштуваннях обрано цей як сценарій помилок) завершився помилкою або перевищив час.");

    /// <summary>Item входу сценарію помилок.</summary>
    /// <param name="executionId">Виконання, що завершилось помилкою.</param>
    /// <param name="status">Його стан (<c>error</c> або <c>timed_out</c>).</param>
    /// <param name="startedAt">Початок виконання.</param>
    /// <param name="finishedAt">Завершення виконання.</param>
    /// <param name="mode">Режим запуску (<c>manual</c>, <c>trigger</c>, <c>retry</c>).</param>
    /// <param name="workflowId">Сценарій.</param>
    /// <param name="workflowName">Назва сценарію.</param>
    /// <param name="message">Повідомлення помилки (без значень секретів).</param>
    /// <param name="nodeId">Вузол, на якому виконання зупинилось; <c>null</c> — помилка не вузла.</param>
    /// <param name="nodeName">Назва цього вузла.</param>
    public static Item CreateItem(
        Guid executionId,
        string status,
        DateTimeOffset? startedAt,
        DateTimeOffset? finishedAt,
        string mode,
        Guid workflowId,
        string workflowName,
        string? message,
        string? nodeId,
        string? nodeName) => new()
        {
            Json = new JsonObject
            {
                ["execution"] = new JsonObject
                {
                    ["id"] = executionId.ToString(),
                    ["status"] = status,
                    ["startedAt"] = startedAt?.ToUniversalTime().ToString("O"),
                    ["finishedAt"] = finishedAt?.ToUniversalTime().ToString("O"),
                    ["mode"] = mode,
                },
                ["workflow"] = new JsonObject
                {
                    ["id"] = workflowId.ToString(),
                    ["name"] = workflowName,
                },
                ["error"] = new JsonObject
                {
                    ["message"] = message,
                    ["nodeId"] = nodeId,
                    ["nodeName"] = nodeName,
                },
            },
        };

    /// <inheritdoc />
    public Task<Item[][]> ExecuteAsync(INodeContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();
        var sample = CreateItem(
            Guid.Empty, "error", null, null, "manual", Guid.Empty, "Приклад", "Тестовий запуск: тут буде помилка сценарію.", null, null);
        return Task.FromResult<Item[][]>([[sample]]);
    }
}
