namespace OpenBP.Api;

/// <summary>Маркер збірки OpenBP.Api; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
