namespace OpenBP.Connectors.PsySupport.Mcp;

/// <summary>
/// Права поточного MCP-виклику (§7.3): телефони віддаються лише за токеном <c>PSY_MCP_TOKEN_PII</c>. stdio — без доступу
/// до телефонів: кабінет психолога в боті (правило 4.8) працює незалежно від цього.
/// </summary>
public sealed class McpAccess(IHttpContextAccessor http)
{
    /// <summary>Ключ у <see cref="HttpContext.Items"/>, який ставить перевірка Bearer-токена.</summary>
    public const string PiiItem = "psy.mcp.pii";

    public bool Pii => http.HttpContext?.Items[PiiItem] is true;
}
