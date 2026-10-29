using OpenBP.Application.Auth;
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
/// <param name="TriggerType">Тип першого увімкненого тригера поточної версії (<c>core.schedule</c> тощо); <c>null</c> — тригера немає (F3-80).</param>
/// <param name="LastExecution">Останнє виконання сценарію; <c>null</c> — сценарій ще не запускали (F3-80).</param>
public sealed record WorkflowSummary(
    Guid Id,
    string Name,
    bool Active,
    int Version,
    DateTimeOffset UpdatedAt,
    Guid? GroupId = null,
    int? PublishedVersion = null,
    string? TriggerType = null,
    WorkflowLastExecution? LastExecution = null);

/// <summary>Останнє виконання в рядку списку сценаріїв (F3-80).</summary>
/// <param name="Id"><c>executions.id</c>.</param>
/// <param name="Status">Стан виконання (<c>success</c>, <c>error</c>, <c>running</c> тощо — як у <c>executions.status</c>).</param>
/// <param name="StartedAt">Початок; <c>null</c> — виконання ще в черзі.</param>
public sealed record WorkflowLastExecution(Guid Id, string Status, DateTimeOffset? StartedAt);

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
/// <param name="Role">Роль поточного користувача в проєкті (F6-14): <c>admin</c> — адміністратор інстансу.</param>
public sealed record WorkflowGroupSummary(Guid Id, string Name, int SortOrder, int WorkflowCount, UserRole? Role = null)
{
    /// <summary>Опис проєкту (F8-03); порожній — без опису.</summary>
    public string Description { get; init; } = "";

    /// <summary>Значок проєкту (F8-03): назва іконки чи emoji; порожній — типовий.</summary>
    public string Icon { get; init; } = "";

    /// <summary>Ролі застосунку проєкту (F8-04).</summary>
    public IReadOnlyList<ProjectAppRole> AppRoles { get; init; } = [];

    /// <summary>Моя роль застосунку в проєкті (F8-04); <c>null</c> — немає.</summary>
    public string? AppRole { get; init; }

    /// <summary>
    /// Оболонка застосунку (F8-15, docs/projects.md §3.9); <c>null</c> — немає. Меню містить усі сторінки — клієнт показує
    /// ті, що є серед видимих йому (<c>GET /projects/{id}/pages</c>).
    /// </summary>
    public Projects.ProjectApp? App { get; init; }
}

/// <summary>Роль застосунку проєкту (F8-04, docs/projects.md §4): незалежна від ролі проєкту editor/viewer.</summary>
/// <param name="Key">Ключ: латиниця в нижньому регістрі, цифри, <c>_</c>, <c>-</c>; унікальний у проєкті.</param>
/// <param name="Name">Назва для людей.</param>
public sealed record ProjectAppRole(string Key, string Name);

/// <summary>Учасник проєкту (<c>GET /workflow-groups/{id}/members</c>, F6-14).</summary>
/// <param name="UserId"><c>users.id</c>.</param>
/// <param name="Email">Email користувача.</param>
/// <param name="Role"><see cref="UserRole.Editor"/> або <see cref="UserRole.Viewer"/> у межах проєкту.</param>
/// <param name="GlobalRole">Глобальна роль користувача (для довідки в UI).</param>
public sealed record ProjectMember(Guid UserId, string Email, UserRole Role, UserRole? GlobalRole)
{
    /// <summary>Роль застосунку (F8-04): ключ з ролей застосунку проєкту або <c>null</c>.</summary>
    public string? AppRole { get; init; }
}

/// <summary>Результат <c>POST /workflow-groups/{id}/members/add</c> (F8-13).</summary>
/// <param name="Member">Учасник з ролями.</param>
/// <param name="Created">Обліковий запис створено цим запитом.</param>
/// <param name="TemporaryPassword">Тимчасовий пароль нового облікового запису — показується один раз; <c>null</c>, якщо
/// запис уже був або створений без пароля (вхід через OIDC).</param>
public sealed record ProjectMemberAdded(ProjectMember Member, bool Created, string? TemporaryPassword);

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
