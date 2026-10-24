using OpenBP.Application.Auth;
using OpenBP.Application.Errors;

namespace OpenBP.Application.Workflows;

/// <summary>
/// Групи сценаріїв (F3-63): плоский список поза визначенням сценарію, формат workflow не змінюється. Один сервіс для REST
/// (<c>/workflow-groups</c>), UI і MCP-tools <c>list_workflow_groups</c>/<c>create_workflow_group</c>; перенесення
/// сценарію між групами — <see cref="IWorkflowService.MoveAsync"/>.
/// <list type="bullet">
/// <item>Назва групи унікальна без урахування регістру; повтор — <see cref="ConflictException"/> з кодом
/// <see cref="WorkflowGroupService.NameTakenCode"/>.</item>
/// <item>Видалення групи лишає її сценарії без групи (у спільному просторі); проєкт з credentials чи конекторами не
/// видаляється (<see cref="WorkflowGroupService.ProjectNotEmptyCode"/>).</item>
/// <item>Група — це проєкт (F6-14, <see cref="ProjectAccess"/>): admin бачить усі, інші — лише ті, де вони учасники.
/// Створює глобальний editor (стає editor-учасником), перейменовує, видаляє й керує учасниками admin або editor проєкту.</item>
/// </list>
/// </summary>
public interface IWorkflowGroupService
{
    /// <summary>Усі групи в порядку <c>sort_order</c>, з кількістю сценаріїв.</summary>
    /// <param name="ct">Скасування.</param>
    Task<IReadOnlyList<WorkflowGroupSummary>> ListAsync(CancellationToken ct);

    /// <summary>Створює групу в кінці списку.</summary>
    /// <param name="name">Назва, непорожня.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ConflictException">Група з такою назвою вже є.</exception>
    Task<WorkflowGroupSummary> CreateAsync(string name, CancellationToken ct);

    /// <summary>Перейменовує групу.</summary>
    /// <param name="id"><c>workflow_groups.id</c>.</param>
    /// <param name="name">Нова назва, непорожня.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Групи немає.</exception>
    /// <exception cref="ConflictException">Інша група з такою назвою вже є.</exception>
    Task<WorkflowGroupSummary> RenameAsync(Guid id, string name, CancellationToken ct);

    /// <summary>Видаляє групу; її сценарії лишаються без групи.</summary>
    /// <param name="id"><c>workflow_groups.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Групи немає.</exception>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Новий порядок груп: <paramref name="ids"/> — усі id груп у бажаному порядку (кожен рівно раз).
    /// </summary>
    /// <param name="ids">Id груп.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="ArgumentException">Список не збігається з наявними групами.</exception>
    Task<IReadOnlyList<WorkflowGroupSummary>> ReorderAsync(IReadOnlyList<Guid> ids, CancellationToken ct);

    /// <summary>Учасники проєкту (F6-14), за email.</summary>
    /// <param name="id"><c>workflow_groups.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає або користувач не його учасник.</exception>
    Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid id, CancellationToken ct);

    /// <summary>Додає користувача до проєкту або змінює його роль (admin або editor проєкту).</summary>
    /// <param name="id"><c>workflow_groups.id</c>.</param>
    /// <param name="email">Email наявного користувача.</param>
    /// <param name="role"><see cref="UserRole.Editor"/> або <see cref="UserRole.Viewer"/>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Проєкту чи користувача немає.</exception>
    /// <exception cref="ForbiddenException">Користувач не admin і не editor проєкту.</exception>
    Task<ProjectMember> SetMemberAsync(Guid id, string email, UserRole role, CancellationToken ct);

    /// <summary>Прибирає користувача з проєкту (admin або editor проєкту).</summary>
    /// <param name="id"><c>workflow_groups.id</c>.</param>
    /// <param name="userId"><c>users.id</c>.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Проєкту чи учасника немає.</exception>
    Task RemoveMemberAsync(Guid id, Guid userId, CancellationToken ct);
}
