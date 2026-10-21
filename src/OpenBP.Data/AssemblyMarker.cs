namespace OpenBP.Data;

/// <summary>Маркер збірки OpenBP.Data; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
