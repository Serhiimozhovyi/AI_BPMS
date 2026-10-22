namespace OpenBP.Application;

/// <summary>Маркер збірки OpenBP.Application; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
