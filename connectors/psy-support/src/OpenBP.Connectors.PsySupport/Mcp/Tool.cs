using ModelContextProtocol;

namespace OpenBP.Connectors.PsySupport.Mcp;

/// <summary>Некоректні аргументи → <see cref="McpException"/> (<c>isError</c>); помилки домену повертаються в результаті.</summary>
internal static class Tool
{
    public static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
