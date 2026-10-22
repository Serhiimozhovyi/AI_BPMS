namespace OpenBP.Expressions;

/// <summary>Маркер збірки OpenBP.Expressions; використовується smoke-тестами.</summary>
public static class AssemblyMarker
{
    /// <summary>Ім'я збірки.</summary>
    public static string Name => typeof(AssemblyMarker).Assembly.GetName().Name!;
}
