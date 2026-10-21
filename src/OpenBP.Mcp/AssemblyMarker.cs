namespace OpenBP.Mcp;

/// <summary>Маркер збірки OpenBP.Mcp; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
