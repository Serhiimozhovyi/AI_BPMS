using System.Security.Claims;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Auth;

/// <summary>Claims обох схем (docs/architecture.md §10.6): <c>sub</c>, <c>email</c>, <c>role</c>.</summary>
public static class OpenBpClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Role = "role";
    public const string ApiKeyId = "api_key_id";
    public const string ApiKeyProjectId = "api_key_project_id";
    public const string ViewAsProject = "view_as_project";
    public const string ViewAsRole = "view_as_role";
    public const string ViewAsAppRole = "view_as_app_role";
    public const string ViewAsActor = "view_as_actor";
    public const string ViewAsActorEmail = "view_as_actor_email";
    public const string ViewAsMember = "view_as_member";

    /// <summary>
    /// Principal режиму перегляду «як користувач» (F12-01): <c>sub</c> і <c>email</c> — того, чиїми очима дивимось, роль —
    /// viewer, а справжній користувач — у <c>view_as_actor*</c>. Ту саму cookie не змінює: живе один запит.
    /// </summary>
    /// <param name="context">Режим.</param>
    /// <param name="userId">Чиїми очима.</param>
    /// <param name="email">Його пошта.</param>
    /// <param name="scheme">Схема автентифікації запиту.</param>
    public static ClaimsPrincipal CreateViewAsPrincipal(ViewAsContext context, Guid userId, string email, string? scheme)
    {
        ArgumentNullException.ThrowIfNull(context);
        var claims = new List<Claim>
        {
            new(Subject, userId.ToString()),
            new(Email, email),
            new(Role, UserRoles.ToValue(UserRole.Viewer)),
            new(ViewAsProject, context.ProjectId.ToString()),
            new(ViewAsRole, UserRoles.ToValue(context.ProjectRole)),
            new(ViewAsActor, context.ActorUserId.ToString()),
            new(ViewAsActorEmail, context.ActorEmail),
            new(ViewAsMember, context.AsMember ? "true" : "false"),
        };
        if (context.AppRole is { } appRole)
        {
            claims.Add(new Claim(ViewAsAppRole, appRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme ?? "ViewAs", Email, Role));
    }

    /// <summary><see cref="ClaimsPrincipal"/> користувача для схеми <paramref name="scheme"/>.</summary>
    /// <param name="user">Користувач.</param>
    /// <param name="scheme">Схема автентифікації.</param>
    public static ClaimsPrincipal CreatePrincipal(UserInfo user, string scheme)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = new ClaimsIdentity(
            [
                new Claim(Subject, user.Id.ToString()),
                new Claim(Email, user.Email),
                new Claim(Role, UserRoles.ToValue(user.Role)),
            ],
            scheme,
            Email,
            Role);
        if (user.ApiKeyId is { } keyId)
        {
            identity.AddClaim(new Claim(ApiKeyId, keyId.ToString()));
        }

        if (user.ApiKeyProjectId is { } projectId)
        {
            identity.AddClaim(new Claim(ApiKeyProjectId, projectId.ToString()));
        }

        return new ClaimsPrincipal(identity);
    }
}

/// <summary><see cref="ICurrentUser"/> з <see cref="HttpContext.User"/> поточного запиту.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    /// <inheritdoc />
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true && UserId is not null && Role is not null;

    /// <inheritdoc />
    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirst(OpenBpClaims.Subject)?.Value, out var id) ? id : null;

    /// <inheritdoc />
    public string? Email => Principal?.FindFirst(OpenBpClaims.Email)?.Value;

    /// <inheritdoc />
    public UserRole? Role => UserRoles.TryParse(Principal?.FindFirst(OpenBpClaims.Role)?.Value);

    /// <inheritdoc />
    public Guid? ApiKeyId =>
        Guid.TryParse(Principal?.FindFirst(OpenBpClaims.ApiKeyId)?.Value, out var id) ? id : null;

    /// <inheritdoc />
    public Guid? ApiKeyProjectId =>
        Guid.TryParse(Principal?.FindFirst(OpenBpClaims.ApiKeyProjectId)?.Value, out var id) ? id : null;

    /// <inheritdoc />
    public ViewAsContext? ViewAs =>
        Principal is { } p
        && Guid.TryParse(p.FindFirst(OpenBpClaims.ViewAsProject)?.Value, out var project)
        && Guid.TryParse(p.FindFirst(OpenBpClaims.ViewAsActor)?.Value, out var actor)
        && UserRoles.TryParse(p.FindFirst(OpenBpClaims.ViewAsRole)?.Value) is { } role
            ? new ViewAsContext(project, role, p.FindFirst(OpenBpClaims.ViewAsAppRole)?.Value, actor,
                p.FindFirst(OpenBpClaims.ViewAsActorEmail)?.Value ?? "", p.FindFirst(OpenBpClaims.ViewAsMember)?.Value == "true")
            : null;
}
