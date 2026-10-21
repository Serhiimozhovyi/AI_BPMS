namespace OpenBP.Engine.Testing;

/// <summary>Маркер збірки OpenBP.Engine.Testing; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
