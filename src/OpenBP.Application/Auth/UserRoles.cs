namespace OpenBP.Application.Auth;

/// <summary>Перетворення <see cref="UserRole"/> ↔ значення <c>users.role</c> (<c>viewer</c>/<c>editor</c>/<c>admin</c>).</summary>
public static class UserRoles
{
    /// <summary>Значення для <c>users.role</c> і claim <c>role</c>.</summary>
    /// <param name="role">Роль.</param>
    public static string ToValue(UserRole role) => role switch
    {
        UserRole.Viewer => "viewer",
        UserRole.Editor => "editor",
        UserRole.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>Розбирає значення <c>users.role</c>; невідоме значення → <c>null</c>.</summary>
    /// <param name="value">Значення.</param>
    public static UserRole? TryParse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "viewer" => UserRole.Viewer,
        "editor" => UserRole.Editor,
        "admin" => UserRole.Admin,
        _ => null,
    };
}
