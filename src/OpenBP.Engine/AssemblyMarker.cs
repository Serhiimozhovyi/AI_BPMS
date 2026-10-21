namespace OpenBP.Engine;

/// <summary>Маркер збірки OpenBP.Engine; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
