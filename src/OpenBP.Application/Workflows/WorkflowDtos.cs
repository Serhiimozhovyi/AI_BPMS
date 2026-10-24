using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Сценарій у списку (<c>GET /workflows</c>).</summary>
/// <param name="Id"><c>workflows.id</c>.</param>
/// <param name="Name">Назва (<c>workflows.name</c>).</param>
/// <param name="Active">Чи активний (тригери зареєстровані).</param>
/// <param name="Version">Номер поточної версії.</param>
/// <param name="UpdatedAt">Останнє збереження, перейменування або зміна активності.</param>
/// <param name="GroupId">Група сценарію (F3-63); <c>null</c> — без групи.</param>
/// <param name="PublishedVersion">Номер опублікованої версії (F6-10); <c>null</c> — сценарій ще не публікувався.</param>
public sealed record WorkflowSummary(Guid Id, string Name, bool Active, int Version, DateTimeOffset UpdatedAt, Guid? GroupId = null, int? PublishedVersion = null);

/// <summary>Сценарій з визначенням чернетки — поточної версії (<c>GET /workflows/{id}</c>).</summary>
/// <param name="Id"><c>workflows.id</c>.</param>
/// <param name="Name">Назва; <see cref="WorkflowDefinition.Name"/> у <paramref name="Definition"/> завжди дорівнює їй.</param>
/// <param name="Active">Чи активний.</param>
/// <param name="Version">Номер поточної версії.</param>
/// <param name="VersionId"><c>workflow_versions.id</c> поточної версії — його отримують виконання.</param>
/// <param name="Definition">Визначення поточної версії (невідомі поля збережені — round-trip).</param>
/// <param name="UpdatedAt">Останнє збереження, перейменування або зміна активності.</param>
/// <param name="GroupId">Група сценарію (F3-63); <c>null</c> — без групи.</param>
/// <param name="PublishedVersion">
/// Номер опублікованої версії (F6-10) — її виконують тригери; <c>null</c> — сценарій ще не публікувався.
/// <see cref="Version"/> — чернетка, яку редагують і запускають тестово.
/// </param>
public sealed record WorkflowDetail(
    Guid Id,
    string Name,
    bool Active,
    int Version,
    Guid VersionId,
    WorkflowDefinition Definition,
    DateTimeOffset UpdatedAt,
    Guid? GroupId = null,
    int? PublishedVersion = null)
{
    /// <summary>Чернетка відрізняється від опублікованої версії (або публікації ще не було).</summary>
    public bool HasUnpublishedChanges => PublishedVersion != Version;
}

/// <summary>Версія в історії (<c>GET /workflows/{id}/versions</c>).</summary>
/// <param name="Id"><c>workflow_versions.id</c>.</param>
/// <param name="Version">Номер версії (з 1, без пропусків).</param>
/// <param name="CreatedAt">Час збереження.</param>
/// <param name="CreatedBy">Хто зберіг; <c>null</c> — системна операція.</param>
/// <param name="CreatedByEmail">Email автора для історії версій (F3-61); <c>null</c> — системна операція або користувача видалено.</param>
public sealed record WorkflowVersionSummary(Guid Id, int Version, DateTimeOffset CreatedAt, Guid? CreatedBy, string? CreatedByEmail = null);

/// <summary>Фільтр списку сценаріїв.</summary>
/// <param name="Search">Підрядок назви без урахування регістру; <c>null</c> — без фільтра.</param>
/// <param name="Active">Лише активні/неактивні; <c>null</c> — усі.</param>
/// <param name="Group">Лише сценарії групи (F3-63); <c>null</c> — усі.</param>
public sealed record WorkflowFilter(string? Search = null, bool? Active = null, WorkflowGroupFilter? Group = null);

/// <summary>Фільтр списку сценаріїв за групою: конкретна група або сценарії без групи.</summary>
/// <param name="GroupId">Id групи; <c>null</c> — сценарії без групи.</param>
public sealed record WorkflowGroupFilter(Guid? GroupId)
{
    /// <summary>Значення параметра запиту для сценаріїв без групи (<c>?groupId=none</c>).</summary>
    public const string NoneValue = "none";

    /// <summary>
    /// Розбирає параметр <c>groupId</c> запиту: порожній — без фільтра (<c>null</c>), <c>none</c> — без групи, інакше id групи.
    /// </summary>
    /// <param name="value">Значення параметра.</param>
    /// <exception cref="ArgumentException">Не <c>none</c> і не id.</exception>
    public static WorkflowGroupFilter? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.Equals(value.Trim(), NoneValue, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkflowGroupFilter((Guid?)null);
        }

        return Guid.TryParse(value, out var id)
            ? new WorkflowGroupFilter(id)
            : throw new ArgumentException($"`groupId` має бути id групи або «{NoneValue}».", nameof(value));
    }
}

/// <summary>Група сценаріїв у списку (<c>GET /workflow-groups</c>).</summary>
/// <param name="Id"><c>workflow_groups.id</c>.</param>
/// <param name="Name">Назва, унікальна без урахування регістру.</param>
/// <param name="SortOrder">Позиція в списку груп, з 0.</param>
/// <param name="WorkflowCount">Скільки сценаріїв у групі.</param>
public sealed record WorkflowGroupSummary(Guid Id, string Name, int SortOrder, int WorkflowCount);

/// <summary>Результат <c>POST /workflows/validate</c>.</summary>
/// <param name="Errors">Помилки; порожній список — сценарій коректний.</param>
public sealed record WorkflowValidationResult(IReadOnlyList<WorkflowValidationError> Errors)
{
    /// <summary><c>true</c>, якщо помилок немає.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// Попередження, що не заважають збереженню й активації: <c>VARIABLE_UNKNOWN</c> — <c>$vars.key</c> без такої змінної
    /// (F6-09; змінну можна створити пізніше, а до того вираз дає <c>undefined</c>).
    /// </summary>
    public IReadOnlyList<WorkflowValidationError> Warnings { get; init; } = [];
}
