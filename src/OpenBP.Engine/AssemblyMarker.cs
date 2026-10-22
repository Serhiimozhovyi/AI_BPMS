namespace OpenBP.Engine;

/// <summary>Маркер збірки OpenBP.Engine; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    /// <summary>Назва збірки.</summary>
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
