using Microsoft.EntityFrameworkCore;
using OpenBP.Application.Errors;
using OpenBP.Application.Workflows;
using OpenBP.Data;

namespace OpenBP.Application.Auth;

/// <summary>
/// Перегляд «як користувач» (F12-01, docs/architecture.md §10.6): розробник проєкту бачить застосунок і AI_BPMS так, як
/// бачить звичайний учасник проєкту з обраною роллю, — з тими самими перевірками прав на сервері. Лише читання: зміни в
/// режимі перегляду відхиляються (<see cref="ViewAsReadOnlyException"/>).
/// </summary>
/// <param name="ProjectId">Проєкт, у якому переглядаємо; інших проєктів і спільного простору в режимі перегляду немає.</param>
/// <param name="ProjectRole">Роль у проєкті: <see cref="UserRole.Editor"/> чи <see cref="UserRole.Viewer"/>.</param>
/// <param name="AppRole">Роль застосунку (ключ з <c>app_roles</c> проєкту) або <c>null</c>.</param>
/// <param name="ActorUserId">Хто насправді переглядає (admin чи editor проєкту).</param>
/// <param name="ActorEmail">Пошта того, хто переглядає.</param>
/// <param name="AsMember">Переглядаємо як конкретний учасник (його id і пошта замість власних, лише admin).</param>
public sealed record ViewAsContext(Guid ProjectId, UserRole ProjectRole, string? AppRole, Guid ActorUserId, string ActorEmail, bool AsMember);

/// <summary>Режим перегляду у відповіді <c>GET /auth/me</c>.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="ProjectRole"><c>editor</c> чи <c>viewer</c>.</param>
/// <param name="AppRole">Роль застосунку або <c>null</c>.</param>
/// <param name="MemberId">Учасник, якого переглядаємо, або <c>null</c> (перегляд за роллю).</param>
/// <param name="ActorEmail">Пошта того, хто переглядає (щоб UI показав «Ви — …»).</param>
public sealed record ViewAsInfo(Guid ProjectId, string ProjectRole, string? AppRole, Guid? MemberId, string ActorEmail);

/// <summary>Розібраний заголовок <c>X-OpenBP-View-As</c>.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="ProjectRole">Роль у проєкті (за замовчуванням viewer).</param>
/// <param name="AppRole">Роль застосунку або <c>null</c>.</param>
/// <param name="MemberId">Учасник для перегляду «як він» або <c>null</c>.</param>
public sealed record ViewAsRequest(Guid ProjectId, UserRole ProjectRole, string? AppRole, Guid? MemberId)
{
    /// <summary>Назва заголовка запиту.</summary>
    public const string Header = "X-OpenBP-View-As";

    /// <summary>
    /// Розбирає значення <c>project=&lt;id&gt;&amp;role=viewer|editor&amp;appRole=&lt;ключ&gt;</c> або
    /// <c>project=&lt;id&gt;&amp;member=&lt;userId&gt;</c>.
    /// </summary>
    /// <param name="value">Значення заголовка.</param>
    /// <exception cref="ViewAsInvalidException">Значення не розібрано.</exception>
    public static ViewAsRequest Parse(string? value)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (value ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? part : part[..eq];
            if (!query.TryAdd(Uri.UnescapeDataString(name), eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..])))
            {
                throw new ViewAsInvalidException($"Параметр «{name}» повторюється.");
            }
        }

        if (!Guid.TryParse(query.TryGetValue("project", out var p) ? p : null, out var project))
        {
            throw new ViewAsInvalidException("Не вказано проєкт (project=<id>).");
        }

        Guid? member = null;
        if (query.TryGetValue("member", out var m))
        {
            if (!Guid.TryParse(m, out var id) || query.ContainsKey("role") || query.ContainsKey("appRole"))
            {
                throw new ViewAsInvalidException("member=<id> не поєднується з role чи appRole: роль береться з учасника.");
            }

            member = id;
        }

        var role = UserRole.Viewer;
        if (query.TryGetValue("role", out var r))
        {
            role = UserRoles.TryParse(r) switch
            {
                UserRole.Viewer => UserRole.Viewer,
                UserRole.Editor => UserRole.Editor,
                _ => throw new ViewAsInvalidException("role — viewer або editor."),
            };
        }

        var appRole = query.TryGetValue("appRole", out var a) && a is { Length: > 0 } key ? key : null;
        return new ViewAsRequest(project, role, appRole, member);
    }
}

/// <summary>Перевіряє, чи може користувач запиту переглядати проєкт «як» іншу роль, і будує <see cref="ViewAsContext"/>.</summary>
public interface IViewAsService
{
    /// <summary>
    /// Права перевіряються за справжнім користувачем запиту: admin — будь-який проєкт і будь-який учасник, editor проєкту —
    /// лише ролі (без учасника). Чужий проєкт — 404, бракує прав — 403 <c>VIEW_AS_FORBIDDEN</c>, невідома роль застосунку
    /// чи не учасник — 400 <c>VIEW_AS_INVALID</c>.
    /// </summary>
    /// <param name="request">Що переглядаємо.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Режим і ціль (id, пошта), чиїми очима дивимось.</returns>
    Task<(ViewAsContext Context, Guid UserId, string Email)> ResolveAsync(ViewAsRequest request, CancellationToken ct);
}

/// <inheritdoc />
public sealed class ViewAsService(OpenBpDbContext db, ICurrentUser currentUser) : IViewAsService
{
    /// <inheritdoc />
    public async Task<(ViewAsContext Context, Guid UserId, string Email)> ResolveAsync(ViewAsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        currentUser.Require(UserRole.Viewer);
        if (currentUser.ApiKeyId is not null || currentUser.ViewAs is not null)
        {
            throw new ViewAsInvalidException("Перегляд «як користувач» — лише для сесії в браузері.");
        }

        var actorId = currentUser.UserId!.Value;
        var actorEmail = currentUser.Email ?? "";
        var admin = currentUser.Role == UserRole.Admin;
        var project = await db.WorkflowGroups.AsNoTracking()
            .Where(g => g.Id == request.ProjectId)
            .Select(g => new { g.Id, g.AppRoles })
            .SingleOrDefaultAsync(ct);
        var actorMembership = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == request.ProjectId && m.UserId == actorId)
            .Select(m => m.Role)
            .SingleOrDefaultAsync(ct);
        if (project is null || (!admin && actorMembership is null))
        {
            throw new NotFoundException("project", request.ProjectId);
        }

        if (!admin && UserRoles.TryParse(actorMembership) != UserRole.Editor)
        {
            throw new ViewAsForbiddenException("Переглядати проєкт як інша роль може admin або editor проєкту.");
        }

        if (request.MemberId is { } memberId)
        {
            if (!admin)
            {
                throw new ViewAsForbiddenException("Переглядати як конкретний учасник може лише admin.");
            }

            var member = await db.ProjectMembers.AsNoTracking()
                .Where(m => m.ProjectId == request.ProjectId && m.UserId == memberId)
                .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.Role, m.AppRole, u.Email })
                .SingleOrDefaultAsync(ct)
                ?? throw new ViewAsInvalidException("Цей користувач не учасник проєкту.");
            var memberRole = UserRoles.TryParse(member.Role) is UserRole.Editor ? UserRole.Editor : UserRole.Viewer;
            return (new ViewAsContext(request.ProjectId, memberRole, string.IsNullOrEmpty(member.AppRole) ? null : member.AppRole,
                actorId, actorEmail, true), memberId, member.Email);
        }

        if (request.AppRole is { } key && ProjectAppRoles.Parse(project.AppRoles).All(r => r.Key != key))
        {
            throw new ViewAsInvalidException($"У проєкті немає ролі застосунку «{key}».");
        }

        return (new ViewAsContext(request.ProjectId, request.ProjectRole, request.AppRole, actorId, actorEmail, false), actorId, actorEmail);
    }
}

/// <summary>400: заголовок <c>X-OpenBP-View-As</c> не розібрано або ціль не існує.</summary>
public sealed class ViewAsInvalidException(string message) : OpenBpException("VIEW_AS_INVALID", 400, message);

/// <summary>403: користувач не може переглядати проєкт «як» іншу роль.</summary>
public sealed class ViewAsForbiddenException(string message) : OpenBpException("VIEW_AS_FORBIDDEN", 403, message);

/// <summary>403: у режимі перегляду дані не змінюються.</summary>
public sealed class ViewAsReadOnlyException()
    : OpenBpException("VIEW_AS_READ_ONLY", 403, "Режим перегляду «як користувач» лише для читання: вийдіть із нього, щоб змінювати дані.");
