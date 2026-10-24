using System.Text.Json;

namespace OpenBP.Application.Auth.Oidc;

/// <summary>
/// Claims токенів провайдера → <see cref="OidcIdentity"/> (F4-14). Працює з сирими JSON-payload-ами id_token і
/// access token: у Keycloak ролі лежать вкладеними об'єктами (<c>realm_access.roles</c>), які
/// <c>ClaimsIdentity</c> розплющує в рядки.
/// </summary>
public static class OidcClaimsMapper
{
    /// <summary>Збирає користувача з payload-ів токенів; перший непорожній claim виграє.</summary>
    /// <param name="provider">Провайдер.</param>
    /// <param name="payloads">Payload-и: спершу id_token, далі access token (якщо JWT) і userinfo.</param>
    public static OidcIdentity Map(OidcProviderOptions provider, IEnumerable<JsonElement> payloads)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var objects = payloads.Where(p => p.ValueKind == JsonValueKind.Object).ToList();

        var subject = FirstString(objects, "sub") ?? "";
        var email = FirstString(objects, "email");
        // Entra ID без optional claim email: UPN має вигляд пошти.
        email ??= new[] { FirstString(objects, "upn"), FirstString(objects, "preferred_username") }
            .FirstOrDefault(v => v?.Contains('@', StringComparison.Ordinal) == true);
        var verified = provider.TrustEmail
                       || objects.Any(o => IsTrue(o, "email_verified"))
                       // Entra ID: optional claim xms_edov — домен пошти підтверджено власником тенанта.
                       || objects.Any(o => IsTrue(o, "xms_edov"));

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in (provider.RoleClaim ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var payload in objects)
            {
                if (TryGetPath(payload, path, out var value))
                {
                    AddStrings(value, roles);
                }
            }
        }

        return new OidcIdentity(subject, string.IsNullOrWhiteSpace(email) ? null : AuthService.NormalizeEmail(email), verified, roles);
    }

    /// <summary>Розбирає payload JWT (без перевірки підпису — лише для читання вже перевірених токенів).</summary>
    /// <param name="jwt">Токен; не-JWT (opaque) → <c>null</c>.</param>
    public static JsonElement? ReadPayload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            var segment = parts[1].Replace('-', '+').Replace('_', '/');
            segment = segment.PadRight(segment.Length + ((4 - (segment.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(segment));
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static bool TryGetPath(JsonElement element, string path, out JsonElement value)
    {
        value = element;
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddStrings(JsonElement value, HashSet<string> into)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            into.Add(value.GetString()!);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
            {
                into.Add(item.GetString()!);
            }
        }
    }

    private static string? FirstString(List<JsonElement> objects, string name) =>
        objects.Select(o => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static bool IsTrue(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
}
