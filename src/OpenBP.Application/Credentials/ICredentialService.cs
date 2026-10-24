using System.Text.Json.Nodes;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Application.Credentials;

/// <summary>Хто посилається на credential.</summary>
/// <param name="Id">Id конектора або сценарію.</param>
/// <param name="Name">Назва.</param>
public sealed record CredentialReference(Guid Id, string Name);

/// <summary>Де використовується credential.</summary>
/// <param name="Connectors">Конектори (<c>mcp_servers.credential_id</c>).</param>
/// <param name="Workflows">Сценарії, поточна версія яких посилається на id (вузли, auth webhook-а).</param>
public sealed record CredentialUsage(IReadOnlyList<CredentialReference> Connectors, IReadOnlyList<CredentialReference> Workflows)
{
    /// <summary>Чи використовується взагалі.</summary>
    public bool Any => Connectors.Count > 0 || Workflows.Count > 0;
}

/// <summary>Credential без значень секретів (<c>GET /credentials</c>, <c>GET /credentials/{id}</c>).</summary>
/// <param name="Id"><c>credentials.id</c>; на нього посилаються конектори й параметри вузлів.</param>
/// <param name="Name">Назва.</param>
/// <param name="Type">Тип (<see cref="CredentialTypes"/>).</param>
/// <param name="Fields">Заповнені поля без значень (<c>apiKey</c>, <c>headers.Authorization</c>).</param>
/// <param name="CreatedBy">Хто створив; <c>null</c> — системний.</param>
/// <param name="UsedBy">Де використовується.</param>
/// <param name="Error">Дані не розшифровуються (інший ключ шифрування): пояснення; інакше <c>null</c>.</param>
public sealed record CredentialSummary(
    Guid Id,
    string Name,
    string Type,
    IReadOnlyList<string> Fields,
    Guid? CreatedBy,
    CredentialUsage UsedBy,
    string? Error);

/// <summary>Тіло <c>POST /credentials</c>.</summary>
/// <param name="Name">Назва, непорожня.</param>
/// <param name="Type">Тип з каталогу.</param>
/// <param name="Data">Дані за схемою типу.</param>
public sealed record CredentialCreateInput(string Name, string Type, JsonObject Data);

/// <summary>Тіло <c>PATCH /credentials/{id}</c>: що не вказано — не змінюється.</summary>
/// <param name="Name">Нова назва.</param>
/// <param name="Data">Часткова заміна даних (<see cref="CredentialTypes.Merge"/>): <c>null</c>-значення видаляє поле.</param>
public sealed record CredentialUpdateInput(string? Name = null, JsonObject? Data = null);

/// <summary>
/// Credentials (docs/architecture.md §8, §10): REST <c>/credentials</c> і сторінка UI. Лише <see cref="UserRole.Admin"/>.
/// Значення секретів назовні не віддаються ніколи — лише назви заповнених полів.
/// </summary>
public interface ICredentialService
{
    /// <summary>Каталог типів.</summary>
    IReadOnlyList<CredentialTypeInfo> Types();

    /// <summary>Усі credentials за назвою.</summary>
    /// <param name="ct">Скасування.</param>
    Task<IReadOnlyList<CredentialSummary>> ListAsync(CancellationToken ct);

    /// <summary>Один credential.</summary>
    /// <param name="id">Id.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Немає.</exception>
    Task<CredentialSummary> GetAsync(Guid id, CancellationToken ct);

    /// <summary>Створює credential.</summary>
    /// <param name="input">Назва, тип, дані.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="CredentialValidationException">Невідомий тип або дані не за схемою.</exception>
    Task<CredentialSummary> CreateAsync(CredentialCreateInput input, CancellationToken ct);

    /// <summary>Перейменовує і/або частково замінює дані.</summary>
    /// <param name="id">Id.</param>
    /// <param name="input">Зміни.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Немає.</exception>
    /// <exception cref="CredentialValidationException">Дані після злиття не за схемою.</exception>
    Task<CredentialSummary> UpdateAsync(Guid id, CredentialUpdateInput input, CancellationToken ct);

    /// <summary>Видаляє credential.</summary>
    /// <param name="id">Id.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Немає.</exception>
    /// <exception cref="ConflictException">Використовується (код <c>CREDENTIAL_IN_USE</c>).</exception>
    Task DeleteAsync(Guid id, CancellationToken ct);
}
