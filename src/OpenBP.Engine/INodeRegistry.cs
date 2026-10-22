using System.Diagnostics.CodeAnalysis;
using OpenBP.Engine.Nodes;

namespace OpenBP.Engine;

/// <summary>
/// Реєстр типів вузлів (docs/architecture.md §2): за <c>type</c> і <c>typeVersion</c> з формату workflow
/// повертає реалізацію <see cref="INode"/>. Старі версії лишаються зареєстрованими, поки є сценарії, що їх використовують.
/// Реалізації: збірка з <c>core.*</c>, <c>mcp.*</c>, <c>ai.llm</c> у хостах (воркер, API, CLI)
/// і <c>InMemoryNodeRegistry</c> у <c>OpenBP.Engine.Testing</c>.
/// </summary>
public interface INodeRegistry
{
    /// <summary>Повертає вузол для типу і версії.</summary>
    /// <param name="type">Тип вузла, напр. <c>core.if</c>.</param>
    /// <param name="typeVersion">Версія типу (≥ 1).</param>
    /// <exception cref="NodeTypeNotFoundException">Тип або версія не зареєстровані.</exception>
    INode Resolve(string type, int typeVersion);

    /// <summary>Повертає вузол для типу і версії без винятку, якщо його немає.</summary>
    /// <param name="type">Тип вузла.</param>
    /// <param name="typeVersion">Версія типу.</param>
    /// <param name="node">Знайдений вузол або <c>null</c>.</param>
    /// <returns><c>true</c>, якщо вузол зареєстровано.</returns>
    bool TryResolve(string type, int typeVersion, [NotNullWhen(true)] out INode? node);

    /// <summary>Описи всіх зареєстрованих вузлів (усі версії) — для палітри канвасу і валідатора.</summary>
    IReadOnlyCollection<NodeDescriptor> Descriptors { get; }
}
