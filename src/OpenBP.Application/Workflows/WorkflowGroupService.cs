using System.Globalization;
using System.Net.Mail;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenBP.Application.Audit;
using OpenBP.Application.Auth;
using OpenBP.Application.Errors;
using OpenBP.Application.Projects;
using OpenBP.Data;

namespace OpenBP.Application.Workflows;

/// <summary>Реалізація <see cref="IWorkflowGroupService"/> над <c>workflow_groups</c> (F3-63) і <c>project_members</c> (F6-14).</summary>
public sealed class WorkflowGroupService(OpenBpDbContext db, ICurrentUser currentUser, TimeProvider time) : IWorkflowGroupService
{
    /// <summary>Код конфлікту: група з такою назвою вже є.</summary>
    public const string NameTakenCode = "WORKFLOW_GROUP_NAME_TAKEN";

    /// <summary>Код конфлікту: у проєкті є credentials, конектори чи таблиці (F8-02) — видалення зробило б їх спільними.</summary>
    public const string ProjectNotEmptyCode = "PROJECT_NOT_EMPTY";

    private readonly ProjectAccess _access = new(db, currentUser);

    /// <summary>Назва сутності в <see cref="NotFoundException"/>.</summary>
    internal const string Entity = "workflow-group";

    /// <summary>Найдовша назва групи.</summary>
    public const int MaxNameLength = 100;

    /// <summary>Найдовший опис проєкту (F8-03).</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>Найдовший значок проєкту (F8-03).</summary>
    public const int MaxIconLength = 40;

    /// <summary>Найбільше ролей застосунку в проєкті (F8-04).</summary>
    public const int MaxAppRoles = 20;

    /// <summary>Код конфлікту: прибрана роль застосунку ще призначена учасникам (F8-04).</summary>
    public const string AppRoleInUseCode = "APP_ROLE_IN_USE";

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowGroupSummary>> ListAsync(CancellationToken ct)
    {
        var scope = await _access.ScopeAsync(ct);
        var ids = scope.Projects.Keys.ToList();
        var rows = await db.WorkflowGroups.AsNoTracking()
            .Where(g => scope.All || ids.Contains(g.Id))
            .OrderBy(g => g.SortOrder).ThenBy(g => g.CreatedAt)
            .Select(g => new { g.Id, g.Name, g.SortOrder, g.Description, g.Icon, g.AppRoles, g.App, Count = db.Workflows.Count(w => w.GroupId == g.Id) })
            .ToListAsync(ct);
        return [.. rows.Select(g => new WorkflowGroupSummary(g.Id, g.Name, g.SortOrder, g.Count, scope.RoleIn(g.Id))
        {
            Description = g.Description,
            Icon = g.Icon,
            AppRoles = ProjectAppRoles.Parse(g.AppRoles),
            AppRole = scope.AppRoleIn(g.Id),
            App = ProjectApps.Parse(g.App),
        })];
    }

    /// <inheritdoc />
    public async Task<WorkflowGroupSummary> CreateAsync(string name, CancellationToken ct, string? description = null, string? icon = null)
    {
        currentUser.Require(UserRole.Editor);
        if (currentUser.ApiKeyProjectId is not null)
        {
            // Ключ проєкту діє лише в ньому — нових проєктів не створює.
            throw new ForbiddenException(UserRole.Admin);
        }

        var trimmed = ValidName(name);
        var normalized = Normalize(trimmed);
        await ThrowIfNameTakenAsync(normalized, null, trimmed, ct);

        var now = time.GetUtcNow();
        var last = await db.WorkflowGroups.MaxAsync(g => (int?)g.SortOrder, ct) ?? -1;
        var group = new WorkflowGroupEntity
        {
            Id = Guid.CreateVersion7(now),
            Name = trimmed,
            NormalizedName = normalized,
            SortOrder = last + 1,
            Description = ValidText(description, MaxDescriptionLength, "Опис проєкту"),
            Icon = ValidText(icon, MaxIconLength, "Значок проєкту"),
            CreatedAt = now,
        };
        db.WorkflowGroups.Add(group);
        var admin = currentUser.Has(UserRole.Admin);
        if (!admin)
        {
            // Автор проєкту — його editor, інакше не-адмін не побачив би щойно створене.
            db.ProjectMembers.Add(new ProjectMemberEntity
            {
                ProjectId = group.Id,
                UserId = currentUser.UserId!.Value,
                Role = UserRoles.ToValue(UserRole.Editor),
                CreatedAt = now,
            });
        }

        db.Audit(currentUser, now, AuditActions.ProjectCreated, AuditEntities.Project, group.Id.ToString(), new JsonObject { ["name"] = trimmed });
        await SaveAsync(trimmed, ct);
        return new WorkflowGroupSummary(group.Id, group.Name, group.SortOrder, 0, admin ? UserRole.Admin : UserRole.Editor)
        {
            Description = group.Description,
            Icon = group.Icon,
        };
    }

    /// <inheritdoc />
    public async Task<WorkflowGroupSummary> UpdateAsync(
        Guid id, string? name, string? description, string? icon, CancellationToken ct, IReadOnlyList<ProjectAppRole>? appRoles = null)
    {
        var group = await FindManagedAsync(id, ct);
        if (appRoles is not null)
        {
            var roles = ProjectAppRoles.Validate(appRoles, MaxAppRoles);
            var keys = roles.Select(r => r.Key).ToList();
            var used = await db.ProjectMembers.AsNoTracking()
                .Where(m => m.ProjectId == id && m.AppRole != null && !keys.Contains(m.AppRole))
                .Select(m => m.AppRole!).Distinct().ToListAsync(ct);
            if (used.Count > 0)
            {
                throw new ConflictException(AppRoleInUseCode,
                    $"Роль застосунку {string.Join(", ", used.Order(StringComparer.Ordinal))} ще призначена учасникам — спершу змініть їм роль.");
            }

            group.AppRoles = ProjectAppRoles.Serialize(roles);
        }

        var trimmed = name is null ? group.Name : ValidName(name);
        var newDescription = description is null ? group.Description : ValidText(description, MaxDescriptionLength, "Опис проєкту");
        var newIcon = icon is null ? group.Icon : ValidText(icon, MaxIconLength, "Значок проєкту");
        var normalized = Normalize(trimmed);
        await ThrowIfNameTakenAsync(normalized, id, trimmed, ct);

        if (group.Name != trimmed)
        {
            db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectRenamed, AuditEntities.Project, id.ToString(),
                new JsonObject { ["name"] = trimmed, ["previousName"] = group.Name });
        }

        group.Name = trimmed;
        group.NormalizedName = normalized;
        group.Description = newDescription;
        group.Icon = newIcon;
        await SaveAsync(trimmed, ct);
        return await SummaryAsync(group, ct);
    }

    /// <inheritdoc />
    public async Task<WorkflowGroupSummary> SetAppAsync(Guid id, ProjectApp? app, CancellationToken ct)
    {
        var group = await FindManagedAsync(id, ct);
        ProjectApp? valid = null;
        if (app is not null)
        {
            var pages = (await db.ProjectPages.AsNoTracking().Where(p => p.ProjectId == id).Select(p => p.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
            valid = ProjectApps.Validate(app, pages);
        }

        group.App = valid is null ? null : ProjectApps.Serialize(valid);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectAppSaved, AuditEntities.Project, id.ToString(),
            new JsonObject { ["title"] = valid?.Title, ["home"] = valid?.Home });
        await db.SaveChangesAsync(ct);
        return await SummaryAsync(group, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var group = await FindManagedAsync(id, ct);
        if (await db.Credentials.AnyAsync(c => c.ProjectId == id, ct) || await db.McpServers.AnyAsync(s => s.ProjectId == id, ct)
            || await db.DataTables.AnyAsync(t => t.ProjectId == id, ct))
        {
            throw new ConflictException(ProjectNotEmptyCode,
                $"У проєкті «{group.Name}» є облікові дані, конектори або таблиці; видаліть або перенесіть їх перед видаленням проєкту.");
        }

        // FK `ON DELETE SET NULL` теж це робить; явне оновлення не залежить від того, чи база вже знає про зв'язок.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Workflows.Where(w => w.GroupId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.GroupId, (Guid?)null), ct);
        db.WorkflowGroups.Remove(group);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectDeleted, AuditEntities.Project, id.ToString(), new JsonObject { ["name"] = group.Name });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkflowGroupSummary>> ReorderAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        currentUser.Require(UserRole.Editor);
        var scope = await _access.ScopeAsync(ct);
        var visible = scope.Projects.Keys.ToList();
        var groups = await db.WorkflowGroups.Where(g => scope.All || visible.Contains(g.Id)).ToListAsync(ct);
        if (ids.Count != groups.Count || ids.Distinct().Count() != ids.Count || !groups.All(g => ids.Contains(g.Id)))
        {
            throw new ArgumentException("`ids` має містити кожну видиму групу рівно один раз.", nameof(ids));
        }

        // Не-адмін бачить лише частину груп: переставляємо їх між тими самими позиціями, чужі лишаються на місцях.
        var slots = groups.Select(g => g.SortOrder).Order().ToList();
        var byId = groups.ToDictionary(g => g.Id);
        for (var i = 0; i < ids.Count; i++)
        {
            byId[ids[i]].SortOrder = scope.All ? i : slots[i];
        }

        await db.SaveChangesAsync(ct);
        return await ListAsync(ct);
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Назва групи не може бути порожньою.", nameof(name));
        }

        return trimmed.Length <= MaxNameLength
            ? trimmed
            : throw new ArgumentException($"Назва групи довша за {MaxNameLength} символів.", nameof(name));
    }

    private static string ValidText(string? text, int maxLength, string what)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length <= maxLength
            ? trimmed
            : throw new ArgumentException($"{what} довший за {maxLength} символів.", nameof(text));
    }

    private static string Normalize(string name) => name.ToUpper(CultureInfo.InvariantCulture);

    private async Task ThrowIfNameTakenAsync(string normalized, Guid? except, string name, CancellationToken ct)
    {
        if (await db.WorkflowGroups.AnyAsync(g => g.NormalizedName == normalized && g.Id != except, ct))
        {
            throw NameTaken(name);
        }
    }

    /// <summary>Зберігає зміни; паралельне створення тієї самої назви впирається в унікальний індекс — теж конфлікт.</summary>
    private async Task SaveAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw NameTaken(name);
        }
    }

    private static ConflictException NameTaken(string name) =>
        new(NameTakenCode, $"Група «{name}» вже є; оберіть іншу назву.");

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectMember>> ListMembersAsync(Guid id, CancellationToken ct)
    {
        await FindVisibleAsync(id, ct);
        var rows = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == id)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.Email, m.Role, m.AppRole, GlobalRole = u.Role })
            .OrderBy(m => m.Email)
            .ToListAsync(ct);
        return [.. rows.Select(m => new ProjectMember(m.UserId, m.Email, UserRoles.TryParse(m.Role) ?? UserRole.Viewer, UserRoles.TryParse(m.GlobalRole))
        {
            AppRole = m.AppRole,
        })];
    }

    /// <inheritdoc />
    public async Task<ProjectMember> SetMemberAsync(Guid id, string email, UserRole role, CancellationToken ct, string? appRole = null)
    {
        if (role is not (UserRole.Editor or UserRole.Viewer))
        {
            throw new ArgumentException("Роль у проєкті — editor або viewer.", nameof(role));
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Потрібен email користувача.", nameof(email));
        }

        var group = await FindManagedAsync(id, ct);
        appRole = string.IsNullOrWhiteSpace(appRole) ? null : appRole.Trim();
        if (appRole is not null && !ProjectAppRoles.Parse(group.AppRoles).Any(r => r.Key == appRole))
        {
            throw new ArgumentException($"У проєкті немає ролі застосунку «{appRole}».", nameof(appRole));
        }

        var normalized = email.Trim();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email.ToLower() == normalized.ToLower(), ct)
                   ?? throw new NotFoundException("user", normalized);
        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == id && m.UserId == user.Id, ct);
        var now = time.GetUtcNow();
        if (member is null)
        {
            member = new ProjectMemberEntity { ProjectId = id, UserId = user.Id, CreatedAt = now };
            db.ProjectMembers.Add(member);
        }

        member.Role = UserRoles.ToValue(role);
        member.AppRole = appRole;
        db.Audit(currentUser, now, AuditActions.ProjectMemberSet, AuditEntities.Project, id.ToString(),
            new JsonObject { ["userId"] = user.Id.ToString(), ["email"] = user.Email, ["role"] = member.Role, ["appRole"] = appRole });
        await db.SaveChangesAsync(ct);
        return new ProjectMember(user.Id, user.Email, role, UserRoles.TryParse(user.Role)) { AppRole = appRole };
    }

    /// <inheritdoc />
    public async Task<ProjectMemberAdded> AddPersonAsync(Guid id, string email, UserRole role, string? appRole, bool withPassword, CancellationToken ct)
    {
        var normalized = AuthService.NormalizeEmail(email ?? "");
        if (!MailAddress.TryCreate(normalized, out var parsed) || parsed.Address != normalized)
        {
            throw new ArgumentException("Некоректна пошта.", nameof(email));
        }

        // Роль і роль застосунку перевіряємо до створення облікового запису, щоб відмова не лишила людину поза проєктом.
        if (role is not (UserRole.Editor or UserRole.Viewer))
        {
            throw new ArgumentException("Роль у проєкті — editor або viewer.", nameof(role));
        }

        var group = await FindManagedAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(appRole) && !ProjectAppRoles.Parse(group.AppRoles).Any(r => r.Key == appRole.Trim()))
        {
            throw new ArgumentException($"У проєкті немає ролі застосунку «{appRole.Trim()}».", nameof(appRole));
        }

        string? password = null;
        var created = false;
        if (!await db.Users.AnyAsync(u => u.Email.ToLower() == normalized, ct))
        {
            password = withPassword ? AuthService.GenerateTemporaryPassword() : null;
            var now = time.GetUtcNow();
            var user = new UserEntity
            {
                Id = Guid.CreateVersion7(now),
                Email = normalized,
                PasswordHash = password is null ? null : AuthService.HashPassword(password),
                Role = UserRoles.ToValue(UserRole.Viewer),
                CreatedAt = now,
                MustChangePassword = password is not null,
            };
            db.Users.Add(user);
            db.Audit(currentUser, now, AuditActions.UserCreated, AuditEntities.User, user.Id.ToString(),
                new JsonObject { ["email"] = normalized, ["projectId"] = id.ToString(), ["password"] = password is not null });
            await db.SaveChangesAsync(ct);
            created = true;
        }

        var member = await SetMemberAsync(id, normalized, role, ct, appRole);
        return new ProjectMemberAdded(member, created, password);
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(Guid id, Guid userId, CancellationToken ct)
    {
        await FindManagedAsync(id, ct);
        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == id && m.UserId == userId, ct)
                     ?? throw new NotFoundException("project-member", userId);
        db.ProjectMembers.Remove(member);
        db.Audit(currentUser, time.GetUtcNow(), AuditActions.ProjectMemberRemoved, AuditEntities.Project, id.ToString(),
            new JsonObject { ["userId"] = userId.ToString() });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Проєкт, видимий користувачу запиту; чужий — 404.</summary>
    private async Task<WorkflowGroupEntity> FindVisibleAsync(Guid id, CancellationToken ct)
    {
        var group = await db.WorkflowGroups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireAsync(id, UserRole.Viewer, Entity, id, ct);
        return group;
    }

    /// <summary>Проєкт, яким керує користувач запиту (admin або editor проєкту): чужий — 404, viewer — 403.</summary>
    private async Task<WorkflowGroupEntity> FindManagedAsync(Guid id, CancellationToken ct)
    {
        var group = await db.WorkflowGroups.SingleOrDefaultAsync(g => g.Id == id, ct) ?? throw new NotFoundException(Entity, id);
        await _access.RequireAsync(id, UserRole.Editor, Entity, id, ct);
        return group;
    }

    private async Task<WorkflowGroupSummary> SummaryAsync(WorkflowGroupEntity group, CancellationToken ct) =>
        new(group.Id, group.Name, group.SortOrder, await db.Workflows.CountAsync(w => w.GroupId == group.Id, ct),
            await _access.RoleInAsync(group.Id, ct))
        {
            Description = group.Description,
            Icon = group.Icon,
            AppRoles = ProjectAppRoles.Parse(group.AppRoles),
            AppRole = await _access.AppRoleInAsync(group.Id, ct),
            App = ProjectApps.Parse(group.App),
        };
}
