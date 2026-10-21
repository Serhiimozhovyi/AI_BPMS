namespace OpenBP.Worker;

/// <summary>Маркер збірки OpenBP.Worker; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
