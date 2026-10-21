namespace OpenBP.Shared;

/// <summary>Маркер збірки OpenBP.Shared; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
