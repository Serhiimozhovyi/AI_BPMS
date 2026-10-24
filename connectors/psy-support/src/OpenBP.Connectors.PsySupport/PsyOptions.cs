namespace OpenBP.Connectors.PsySupport;

/// <summary>Налаштування застосунку (docs/psy-support.md §2); секрети — лише зі змінних середовища.</summary>
public sealed class PsyOptions
{
    /// <summary>Часовий пояс правил і показу (<c>PSY_TIMEZONE</c>).</summary>
    public string Timezone { get; set; } = "Europe/Kyiv";

    /// <summary>Bearer-токен <c>/mcp</c> без доступу до телефонів (<c>PSY_MCP_TOKEN</c>).</summary>
    public string? McpToken { get; set; }

    /// <summary>Bearer-токен <c>/mcp</c> з доступом до телефонів (<c>PSY_MCP_TOKEN_PII</c>).</summary>
    public string? McpTokenPii { get; set; }

    /// <summary>Перший координатор (<c>PSY_ADMIN_EMAIL</c>).</summary>
    public string? AdminEmail { get; set; }

    /// <summary>Пароль першого координатора (<c>PSY_ADMIN_PASSWORD</c>).</summary>
    public string? AdminPassword { get; set; }

    /// <summary>Зчитує змінні середовища <c>PSY_*</c>.</summary>
    /// <param name="environment">Джерело змінних.</param>
    public static PsyOptions FromEnvironment(Func<string, string?> environment) => new()
    {
        Timezone = environment("PSY_TIMEZONE") is { Length: > 0 } tz ? tz : "Europe/Kyiv",
        McpToken = Empty(environment("PSY_MCP_TOKEN")),
        McpTokenPii = Empty(environment("PSY_MCP_TOKEN_PII")),
        AdminEmail = Empty(environment("PSY_ADMIN_EMAIL")),
        AdminPassword = Empty(environment("PSY_ADMIN_PASSWORD")),
    };

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
