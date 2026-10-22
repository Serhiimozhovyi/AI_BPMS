namespace OpenBP.Nodes.Core;

/// <summary>Маркер збірки OpenBP.Nodes.Core; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    /// <summary>Назва збірки.</summary>
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
