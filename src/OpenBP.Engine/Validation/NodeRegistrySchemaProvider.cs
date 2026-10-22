using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Engine.Validation;

/// <summary>
/// Адаптер «<see cref="INodeRegistry"/> → <see cref="INodeSchemaProvider"/>» для вузлів з дескриптором
/// (<c>core.*</c> та інші зареєстровані): порти і тригери — з <c>Descriptor.Inputs/Outputs</c>,
/// параметри (правило 5) — за <c>Descriptor.ParametersSchema</c>.
/// </summary>
public sealed class NodeRegistrySchemaProvider : INodeSchemaProvider
{
    private readonly INodeRegistry _registry;

    /// <summary>Створює адаптер над реєстром вузлів.</summary>
    public NodeRegistrySchemaProvider(INodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <inheritdoc />
    public NodePorts? GetPorts(string type, int typeVersion) =>
        _registry.TryResolve(type, typeVersion, out var node)
            ? new NodePorts(node.Descriptor.Inputs.Count, node.Descriptor.Outputs.Count)
            : null;

    /// <summary>
    /// Параметри вузла за схемою дескриптора; невідомий тип або версія — <see cref="WorkflowValidationCodes.NodeTypeUnknown"/>.
    /// Шляхи помилок відносні до вузла (<c>/parameters/…</c>).
    /// </summary>
    public Task<IReadOnlyList<WorkflowValidationError>> ValidateNodeAsync(NodeDefinition node, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ct.ThrowIfCancellationRequested();
        if (!_registry.TryResolve(node.Type, node.TypeVersion, out var resolved))
        {
            return Task.FromResult<IReadOnlyList<WorkflowValidationError>>(
            [
                new(WorkflowValidationCodes.NodeTypeUnknown,
                    $"Тип вузла «{node.Type}» версії {node.TypeVersion} не зареєстровано.", node.Id, "/type"),
            ]);
        }

        return Task.FromResult(ParameterSchemaValidator.Validate(
            resolved.Descriptor.ParametersSchema, node.Parameters, node.Id, "/parameters"));
    }
}
