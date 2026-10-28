using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Data;

namespace OpenBP.Application.Auth;

/// <summary>
/// Права в межах проєктів (F6-14, docs/architecture.md §10.6). Проєкт — це група сценаріїв F3-63
/// (<c>workflow_groups</c>); <c>null</c> — спільний простір.
/// <list type="bullet">
/// <item>admin бачить і змінює все; глобальні editor/viewer у спільному просторі мають свою глобальну роль, а в проєкті —
/// роль учасника з <c>project_members</c> (не учасник — проєкту для нього немає).</item>
/// <item>API-ключ з <c>project_id</c> обмежений цим проєктом: спільного простору й інших проєктів для нього немає,
/// роль — та, що має власник ключа в проєкті.</item>
/// <item>Спільні credentials і конектори (<c>project_id = null</c>) змінює лише admin, а посилатися на них можуть усі.</item>
/// </list>
/// Сервіси application-шару створюють екземпляр на запит, тож REST, UI і MCP перевіряють права однаково.
/// </summary>
public sealed class ProjectAccess(OpenBpDbContext db, ICurrentUser currentUser)
{
    private ProjectScope? _scope;

    /// <summary>Що бачить користувач запиту; рахується один раз на екземпляр.</summary>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="UnauthenticatedException">Запит не автентифіковано.</exception>
    public async Task<ProjectScope> ScopeAsync(CancellationToken ct)
    {
        if (_scope is not null)
        {
            return _scope;
        }

        currentUser.Require(UserRole.Viewer);
        var role = currentUser.Role!.Value;
        var keyProject = currentUser.ApiKeyProjectId;
        if (role == UserRole.Admin)
        {
            return _scope = keyProject is { } only
                ? new ProjectScope(false, new Dictionary<Guid, UserRole> { [only] = UserRole.Admin }, null)
                : new ProjectScope(true, new Dictionary<Guid, UserRole>(), UserRole.Admin);
        }

        var userId = currentUser.UserId!.Value;
        var members = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId && (keyProject == null || m.ProjectId == keyProject))
            .Select(m => new { m.ProjectId, m.Role, m.AppRole })
            .ToListAsync(ct);
        var valid = members.Where(m => UserRoles.TryParse(m.Role) is UserRole.Viewer or UserRole.Editor).ToList();
        var projects = valid.ToDictionary(m => m.ProjectId, m => UserRoles.TryParse(m.Role)!.Value);
        var appRoles = valid.Where(m => !string.IsNullOrEmpty(m.AppRole)).ToDictionary(m => m.ProjectId, m => m.AppRole!);
        return _scope = new ProjectScope(false, projects, keyProject is null ? role : null) { AppRoles = appRoles };
    }

    /// <summary>Роль у проєкті <paramref name="projectId"/> (<c>null</c> — спільний простір); <c>null</c> — немає доступу.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<UserRole?> RoleInAsync(Guid? projectId, CancellationToken ct) => (await ScopeAsync(ct)).RoleIn(projectId);

    /// <summary>Роль застосунку користувача запиту в проєкті (F8-04); <c>null</c> — немає (або це admin без членства).</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<string?> AppRoleInAsync(Guid projectId, CancellationToken ct) => (await ScopeAsync(ct)).AppRoleIn(projectId);

    /// <summary>
    /// Чи бачить користувач запиту значення чутливих колонок таблиці проєкту <paramref name="projectId"/>: admin, editor
    /// проєкту й учасник з роллю застосунку (F8-02, F8-04).
    /// </summary>
    /// <param name="projectId">Проєкт таблиці; <c>null</c> — спільна.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<bool> CanReadSensitiveAsync(Guid? projectId, CancellationToken ct) => (await ScopeAsync(ct)).CanReadSensitive(projectId);

    /// <summary>
    /// Вимагає роль не нижчу за <paramref name="minimum"/> у проєкті. Чужий проєкт виглядає як відсутня сутність
    /// (<see cref="NotFoundException"/>), щоб не розкривати її існування.
    /// </summary>
    /// <param name="projectId">Проєкт сутності.</param>
    /// <param name="minimum">Мінімальна роль.</param>
    /// <param name="entity">Назва сутності для <see cref="NotFoundException"/>.</param>
    /// <param name="id">Id сутності для <see cref="NotFoundException"/>.</param>
    /// <param name="ct">Скасування.</param>
    public async Task RequireAsync(Guid? projectId, UserRole minimum, string entity, object id, CancellationToken ct)
    {
        var role = await RoleInAsync(projectId, ct) ?? throw new NotFoundException(entity, id);
        if (role < minimum)
        {
            throw new ForbiddenException(minimum);
        }
    }

    /// <summary>
    /// Чи може користувач запиту змінювати спільний чи проєктний ресурс (credential, конектор): спільний — лише admin
    /// без прив'язки ключа до проєкту, проєктний — editor проєкту.
    /// </summary>
    /// <param name="projectId">Проєкт ресурсу.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<bool> CanManageAsync(Guid? projectId, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        return projectId is null ? scope.All : scope.RoleIn(projectId) >= UserRole.Editor;
    }

    /// <summary>Як <see cref="CanManageAsync"/>, але кидає: чужий проєкт — <see cref="NotFoundException"/>, бракує ролі — <see cref="ForbiddenException"/>.</summary>
    /// <param name="projectId">Проєкт ресурсу.</param>
    /// <param name="entity">Назва сутності.</param>
    /// <param name="id">Id сутності.</param>
    /// <param name="ct">Скасування.</param>
    public async Task RequireManageAsync(Guid? projectId, string entity, object id, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        if (projectId is not null && scope.RoleIn(projectId) is null)
        {
            throw new NotFoundException(entity, id);
        }

        if (!await CanManageAsync(projectId, ct))
        {
            throw new ForbiddenException(projectId is null ? UserRole.Admin : UserRole.Editor);
        }
    }

    /// <summary>Сценарії, які бачить користувач запиту.</summary>
    /// <param name="query">Запит до <c>workflows</c>.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IQueryable<WorkflowEntity>> VisibleWorkflowsAsync(IQueryable<WorkflowEntity> query, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        if (scope.All)
        {
            return query;
        }

        var ids = scope.Projects.Keys.ToList();
        var shared = scope.SharedRole is not null;
        return query.Where(w => (shared && w.GroupId == null) || (w.GroupId != null && ids.Contains(w.GroupId.Value)));
    }

    /// <summary>
    /// Проєкти, ресурси яких (credentials, конектори) видно користувачу запиту як посилання: спільні (<c>null</c>) — завжди,
    /// проєктні — лише учасникам. <c>null</c> у результаті — без обмежень (admin).
    /// </summary>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<Guid>?> VisibleProjectsAsync(CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        return scope.All ? null : [.. scope.Projects.Keys];
    }
}

/// <summary>Знімок прав користувача запиту в проєктах.</summary>
/// <param name="All">admin без прив'язки ключа: усі проєкти й спільний простір з роллю admin.</param>
/// <param name="Projects">Проєкти, де користувач — учасник, з його роллю (для admin з ключем проєкту — цей проєкт).</param>
/// <param name="SharedRole">Роль у спільному просторі; <c>null</c> — спільного простору немає (ключ проєкту).</param>
public sealed record ProjectScope(bool All, IReadOnlyDictionary<Guid, UserRole> Projects, UserRole? SharedRole)
{
    /// <summary>Роль у проєкті (<c>null</c> — спільний простір); <c>null</c> — немає доступу.</summary>
    /// <param name="projectId">Проєкт.</param>
    public UserRole? RoleIn(Guid? projectId) =>
        All ? UserRole.Admin
        : projectId is { } id ? Projects.TryGetValue(id, out var role) ? role : null
        : SharedRole;

    /// <summary>Ролі застосунку в проєктах, де вони є (F8-04): проєкт → ключ ролі.</summary>
    public IReadOnlyDictionary<Guid, string> AppRoles { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Роль застосунку в проєкті; <c>null</c> — немає.</summary>
    /// <param name="projectId">Проєкт.</param>
    public string? AppRoleIn(Guid projectId) => AppRoles.TryGetValue(projectId, out var role) ? role : null;

    /// <summary>Чи видно значення чутливих колонок таблиці проєкту: admin, editor проєкту або роль застосунку в ньому.</summary>
    /// <param name="projectId">Проєкт таблиці; <c>null</c> — спільна.</param>
    public bool CanReadSensitive(Guid? projectId) =>
        RoleIn(projectId) >= UserRole.Editor || (projectId is { } id && RoleIn(id) is not null && AppRoleIn(id) is not null);
}
