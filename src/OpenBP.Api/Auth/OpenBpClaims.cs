using System.Security.Claims;
using OpenBP.Application.Auth;

namespace OpenBP.Api.Auth;

/// <summary>Claims обох схем (docs/architecture.md §10.6): <c>sub</c>, <c>email</c>, <c>role</c>.</summary>
public static class OpenBpClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Role = "role";

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
}
