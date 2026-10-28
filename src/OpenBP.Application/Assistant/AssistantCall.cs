using OpenBP.Application.Auth;

namespace OpenBP.Application.Assistant;

/// <summary>
/// Позначка «через помічника» (F3-113): виклики інструментів загального помічника йдуть у потоці, де
/// <see cref="Active"/> = <c>true</c>. За нею сервіси приховують значення чутливих колонок (модель їх не бачить), а журнал
/// аудиту додає до запису <c>details.via = "assistant"</c>.
/// </summary>
public static class AssistantCall
{
    /// <summary>Значення <c>details.via</c> у журналі аудиту для змін через помічника.</summary>
    public const string Via = "assistant";

    private static readonly AsyncLocal<bool> Current = new();

    /// <summary>Поточний виклик зроблено інструментом загального помічника.</summary>
    public static bool Active => Current.Value;

    /// <summary>
    /// Позначає поточний потік і все, що з нього запускається (зокрема MCP-сервер у процесі); <see cref="Scope.Dispose"/>
    /// повертає попереднє значення лише цьому потоку — запущені задачі зберігають позначку.
    /// </summary>
    public static Scope Enter()
    {
        var previous = Current.Value;
        Current.Value = true;
        return new Scope(previous);
    }

    /// <summary>Область позначки.</summary>
    /// <param name="previous">Значення до <see cref="Enter"/>.</param>
    public readonly struct Scope(bool previous) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => Current.Value = previous;
    }

    /// <summary>
    /// Мінімальна роль для інструмента помічника: читання — viewer, зміни — editor. Інструмент, недоступний за роллю,
    /// помічнику не пропонується й не викликається. Дій лише для admin (конектори, credentials, користувачі, ключі,
    /// змінні) серед інструментів MCP немає, тож помічник їх не має взагалі.
    /// </summary>
    /// <param name="tool">Інструмент.</param>
    public static UserRole MinimumRole(AssistantToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.ReadOnly ? UserRole.Viewer : UserRole.Editor;
    }
}
