namespace OpenBP.Ai;

/// <summary>Маркер збірки OpenBP.Ai; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    /// <summary>Назва збірки.</summary>
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
