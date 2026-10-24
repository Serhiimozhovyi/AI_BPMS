using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Application.Variables;

/// <summary>Змінна інстансу для REST і UI (<c>/settings</c>).</summary>
/// <param name="Key">Ключ (<c>$vars.key</c>).</param>
/// <param name="Value">Значення; не секрет.</param>
/// <param name="Description">Опис для людей.</param>
/// <param name="UpdatedAt">Остання зміна.</param>
public sealed record VariableDto(string Key, string Value, string? Description, DateTimeOffset UpdatedAt);

/// <summary>
/// Змінні інстансу (F6-09): <c>$vars.key</c> у виразах і <c>core.code</c>. Значення — не секрети (секрети лишаються
/// в credentials), тож читати їх може кожен. Ролі: читання — <see cref="UserRole.Viewer"/>, зміни — <see cref="UserRole.Admin"/>.
/// </summary>
public interface IVariableService
{
    /// <summary>Усі змінні за ключем.</summary>
    /// <param name="ct">Скасування.</param>
    Task<IReadOnlyList<VariableDto>> ListAsync(CancellationToken ct);

    /// <summary>Створює або змінює змінну.</summary>
    /// <param name="key">Ключ: латиниця, цифри, <c>_</c>, не з цифри, до <see cref="VariableService.MaxKeyLength"/> символів.</param>
    /// <param name="value">Значення, до <see cref="VariableService.MaxValueLength"/> символів.</param>
    /// <param name="description">Опис; <c>null</c> — без опису.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Ключ або значення не проходять перевірку.</exception>
    Task<VariableDto> SetAsync(string key, string value, string? description, CancellationToken ct);

    /// <summary>Видаляє змінну.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Змінної немає.</exception>
    Task DeleteAsync(string key, CancellationToken ct);
}
