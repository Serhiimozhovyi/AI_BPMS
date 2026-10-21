namespace OpenBP.Cli;

/// <summary>Маркер збірки OpenBP.Cli; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
