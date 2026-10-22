using OpenBP.Shared.Validation;
using OpenBP.Shared.Workflow;

namespace OpenBP.Application.Workflows;

/// <summary>Сценарій у списку (<c>GET /workflows</c>).</summary>
/// <param name="Id"><c>workflows.id</c>.</param>
/// <param name="Name">Назва (<c>workflows.name</c>).</param>
/// <param name="Active">Чи активний (тригери зареєстровані).</param>
/// <param name="Version">Номер поточної версії.</param>
/// <param name="UpdatedAt">Останнє збереження, перейменування або зміна активності.</param>
public sealed record WorkflowSummary(Guid Id, string Name, bool Active, int Version, DateTimeOffset UpdatedAt);

/// <summary>Сценарій з визначенням поточної версії (<c>GET /workflows/{id}</c>).</summary>
/// <param name="Id"><c>workflows.id</c>.</param>
/// <param name="Name">Назва; <see cref="WorkflowDefinition.Name"/> у <paramref name="Definition"/> завжди дорівнює їй.</param>
/// <param name="Active">Чи активний.</param>
/// <param name="Version">Номер поточної версії.</param>
/// <param name="VersionId"><c>workflow_versions.id</c> поточної версії — його отримують виконання.</param>
/// <param name="Definition">Визначення поточної версії (невідомі поля збережені — round-trip).</param>
/// <param name="UpdatedAt">Останнє збереження, перейменування або зміна активності.</param>
public sealed record WorkflowDetail(
    Guid Id,
    string Name,
    bool Active,
    int Version,
    Guid VersionId,
    WorkflowDefinition Definition,
    DateTimeOffset UpdatedAt);

/// <summary>Версія в історії (<c>GET /workflows/{id}/versions</c>).</summary>
/// <param name="Id"><c>workflow_versions.id</c>.</param>
/// <param name="Version">Номер версії (з 1, без пропусків).</param>
/// <param name="CreatedAt">Час збереження.</param>
/// <param name="CreatedBy">Хто зберіг; <c>null</c> — системна операція.</param>
public sealed record WorkflowVersionSummary(Guid Id, int Version, DateTimeOffset CreatedAt, Guid? CreatedBy);

/// <summary>Фільтр списку сценаріїв.</summary>
/// <param name="Search">Підрядок назви без урахування регістру; <c>null</c> — без фільтра.</param>
/// <param name="Active">Лише активні/неактивні; <c>null</c> — усі.</param>
public sealed record WorkflowFilter(string? Search = null, bool? Active = null);

/// <summary>Результат <c>POST /workflows/validate</c>.</summary>
/// <param name="Errors">Помилки; порожній список — сценарій коректний.</param>
public sealed record WorkflowValidationResult(IReadOnlyList<WorkflowValidationError> Errors)
{
    /// <summary><c>true</c>, якщо помилок немає.</summary>
    public bool IsValid => Errors.Count == 0;
}
